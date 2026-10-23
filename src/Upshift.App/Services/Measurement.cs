using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Upshift.Core.Install;
using Upshift.Core.Measure;
using Upshift.Core.Models;

namespace Upshift.App.Services;

public enum MeasureState { Idle, Preparing, Armed, Measuring }

/// <summary>
/// "Measure performance": one measurement at a time, for one game.
/// 1. Arm: PresentMon is downloaded the first time (from its GitHub releases), then, unless this account can already
///    read frame timings, Windows is asked once for permission to start the measuring helper (Upshift.exe --measure,
///    elevated); the main window stays a normal app. The hotkey is registered.
/// 2. The hotkey (F10 by default), or "Start in 10 seconds" in Upshift's window for games that swallow the key, starts PresentMon for the game's process only, for the length the user chose (10 to
///    1800 seconds, 60 by default), with a beep at the start and at the end. The same key stops it early, keeping what
///    was recorded.
/// 3. The CSV is read, its folder deleted, and the run saved with the settings the game had. Nothing leaves the PC.
/// Changed is raised on the UI thread.
/// </summary>
public static class Measurement
{
    private const int ErrorCancelled = 1223;
    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private static GlobalHotkey? _hotkey;
    private static MeasureJob? _job;
    private static string? _directWorkDir;
    private static GameInfo? _game;
    private static Func<MeasuredSettings>? _snapshot;
    private static CancellationTokenSource? _armCts;
    /// <summary>Stops a direct (no helper) run early, keeping its frames.</summary>
    private static CancellationTokenSource? _stopCts;
    /// <summary>The overlay while measuring, its Shift+key, and the loop that feeds it.</summary>
    private static MeasureOverlay? _overlay;
    private static GlobalHotkey? _overlayKey;
    private static CancellationTokenSource? _overlayCts;
    /// <summary>This measurement's length in seconds (the user's choice, 10 to 1800).</summary>
    private static int _seconds = MeasureThresholds.MeasureSeconds;

    /// <summary>The length the next measurement will have: the last value typed (10 to 1800 seconds).</summary>
    public static int ChosenSeconds => MeasureThresholds.ClampSeconds(AppServices.Settings.Current.MeasureSeconds);

    /// <summary>When "Start in 10 seconds" will start the measurement, or null when no countdown runs.</summary>
    public static DateTime? CountdownUntilUtc { get; private set; }

    /// <summary>The countdown "Start in 10 seconds" gives, to switch to the game.</summary>
    public const int CountdownSeconds = 10;

    /// <summary>When the current run started (for the elapsed time), or null.</summary>
    public static DateTime? MeasuringSinceUtc { get; private set; }
    public static int CurrentSeconds => _seconds;

    public static MeasureState State { get; private set; } = MeasureState.Idle;
    /// <summary>The game the current measurement is for (null when idle).</summary>
    public static string? GameId => _game?.Id;
    /// <summary>What to do next, or why the last measurement didn't work.</summary>
    public static string Message { get; private set; } = "";
    /// <summary>The game the last message is about.</summary>
    public static string? MessageGameId { get; private set; }
    public static bool MessageIsProblem { get; private set; }

    public static event Action? Changed;

    public static string HotkeyName => GlobalHotkey.KeyName(AppServices.Settings.Current.MeasureHotkey);

    /// <summary>The key that hides and shows the overlay: Shift + the measuring key.</summary>
    public static string OverlayKeyName => "Shift+" + HotkeyName;

    /// <summary>Said once per game, when its exclusive fullscreen keeps the overlay from showing; null otherwise.</summary>
    public static string? OverlayNotice { get; private set; }
    public static string? OverlayNoticeGameId { get; private set; }
    public const string ExclusiveFullscreenText = "Overlay needs borderless or windowed mode in the game's settings.";

    /// <summary>The sentence shown before Windows' prompt.</summary>
    public static string PermissionExplanation =>
        "Windows only lets programs read a game's frame timings with administrator permission. Windows will ask you to allow " +
        "\"Upshift\" next: that permission goes only to a small measuring helper, which closes when the measurement ends. " +
        "Upshift itself keeps running as a normal app.";

    /// <summary>True when Windows will show its permission prompt for this measurement.</summary>
    public static bool NeedsPermissionPrompt => !PresentMonRunner.CanCaptureWithoutPrompt();

    /// <summary>
    /// Gets everything ready for a measurement of this game and waits for the hotkey. <paramref name="snapshot"/>
    /// reads the game's settings when the measurement starts.
    /// </summary>
    public static async Task ArmAsync(GameInfo game, Func<MeasuredSettings> snapshot)
    {
        Cancel(silent: true);
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _game = game;
        _snapshot = snapshot;
        _seconds = ChosenSeconds;
        _armCts = new CancellationTokenSource();
        var ct = _armCts.Token;
        Set(MeasureState.Preparing, "Getting PresentMon ready…", false);

        // The hotkey mustn't be one of OptiScaler's keys in this game.
        var key = AppServices.Settings.Current.MeasureHotkey;
        if (OptiScalerKeys(game).FirstOrDefault(k => k.Vk == key) is { Name: { } clash })
        {
            Finish($"{HotkeyName} is OptiScaler's {clash} key in this game. Choose another measuring key in Settings.", problem: true);
            return;
        }

        string presentMon;
        try
        {
            var component = AppServices.Catalog.Components.First(c => c.Id == "presentmon");
            var cached = AppServices.Components.TryGetCached(component)
                ?? await AppServices.Components.EnsureAsync(component, AppServices.Updates.Target(component)?.Tag ?? component.PinnedVersion!,
                    new Progress<string>(s => Set(MeasureState.Preparing, $"Getting PresentMon: {s}", false)), ct);
            presentMon = Directory.EnumerateFiles(cached.Folder, "PresentMon*.exe").First();
            if (SignatureCheck.Problem(presentMon, PresentMonRunner.IntelSigners) is { } signature)
            {
                Finish($"The downloaded PresentMon isn't signed by Intel, so it won't be run. {signature}", problem: true);
                return;
            }
        }
        catch (Exception ex) when (ex is Core.Components.ComponentDownloadException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            Finish($"PresentMon couldn't be downloaded: {ex.Message}", problem: true);
            return;
        }
        if (ct.IsCancellationRequested) return;

        var work = Path.Combine(AppServices.DataDir, "measure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        if (PresentMonRunner.CanCaptureWithoutPrompt())
        {
            // This account may already read frame timings (Performance Log Users): no helper, no prompt.
            _directWorkDir = work;
            _job = new MeasureJob { PresentMonPath = presentMon, WorkDir = work, Seconds = _seconds };
        }
        else
        {
            _job = new MeasureJob
            {
                PresentMonPath = presentMon, WorkDir = work, ParentProcessId = Environment.ProcessId, Seconds = _seconds,
                DeadlineUtc = DateTime.UtcNow.AddMinutes(30)
            };
            var jobPath = Path.Combine(work, "job.json");
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(_job), ct);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = $"--measure \"{jobPath}\" --sha256 {OptiScalerInstaller.Sha256(jobPath)}",
                    UseShellExecute = true, Verb = "runas"
                })?.Dispose();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                DeleteWorkDir(TakeWorkDir());
                Finish("Windows permission was refused, so nothing can be measured. Press Measure this game to try again.", problem: true);
                return;
            }
        }

        // Without the key (nothing could watch it), "Start in 10 seconds" still works.
        _hotkey = GlobalHotkey.TryRegister(key);
        if (_hotkey is not null) _hotkey.Pressed += () => _ui?.TryEnqueue(async () => await StartAsync());
        Set(MeasureState.Armed,
            (_hotkey is null
                ? $"{HotkeyName} can't be watched right now, so use Start in {CountdownSeconds} seconds here instead. "
                : $"Waiting for you to press {HotkeyName} in the game. Start {game.Name}, get to a normal gameplay area, then press {HotkeyName}. ") +
            $"If the game doesn't react to {HotkeyName}, press Start in {CountdownSeconds} seconds here and switch to the game. " +
            $"You'll hear a beep when measuring starts and two when it ends ({Length(_seconds)}). Waits up to 30 minutes.", false);
    }

    /// <summary>
    /// The way in that needs no key, for games that keep keys to themselves: the measurement starts on its own after
    /// <see cref="CountdownSeconds"/> seconds, which is time to switch back to the game. The hotkey still works meanwhile.
    /// </summary>
    public static async Task StartInAsync()
    {
        if (State != MeasureState.Armed || CountdownUntilUtc is not null || _armCts is not { } cts) return;
        CountdownUntilUtc = DateTime.UtcNow.AddSeconds(CountdownSeconds);
        Set(MeasureState.Armed, $"Measuring starts in {CountdownSeconds} seconds. Switch to {_game?.Name ?? "the game"} now and keep playing; " +
                                "you'll hear a beep when it starts.", false);
        try { await Task.Delay(TimeSpan.FromSeconds(CountdownSeconds), cts.Token); }
        catch (OperationCanceledException) { return; }
        if (CountdownUntilUtc is null || State != MeasureState.Armed) return;   // cancelled, or the hotkey started it
        await StartAsync();
    }

    /// <summary>"60 seconds", "5 minutes", "2 minutes 30 seconds".</summary>
    public static string Length(double seconds)
    {
        var s = (int)Math.Round(seconds);
        if (s < 120) return s == 1 ? "1 second" : $"{s} seconds";
        var m = s / 60;
        var rest = s % 60;
        return rest == 0 ? $"{m} minutes" : $"{m} minutes {rest} seconds";
    }

    /// <summary>
    /// The hotkey pressed again during a run: stop now and keep what was recorded (direct runs through their token, the
    /// helper through its stop signal).
    /// </summary>
    public static void StopEarly()
    {
        if (State != MeasureState.Measuring || _job is not { } job) return;
        _hotkey?.Dispose();
        _hotkey = null;
        if (_directWorkDir is not null) _stopCts?.Cancel();
        else try { File.WriteAllText(job.StopFile, "stop"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Set(MeasureState.Measuring, "Stopping and keeping what was recorded…", false);
    }

    /// <summary>Stops waiting (or a measurement in progress). The helper sees the cancel signal and quits.</summary>
    public static void Cancel(bool silent = false)
    {
        StopOverlay();
        _armCts?.Cancel();
        CountdownUntilUtc = null;
        _hotkey?.Dispose();
        _hotkey = null;
        if (_job is not null)
            try { File.WriteAllText(_job.CancelFile, "cancel"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var wasActive = State != MeasureState.Idle;
        // The helper needs a moment to see the cancel signal; then its folder goes.
        var dir = TakeWorkDir();
        _ = Task.Run(async () => { await Task.Delay(1500); DeleteWorkDir(dir); });
        if (!silent && wasActive) Finish("Measuring was cancelled.", problem: false);
    }

    private static async Task StartAsync()
    {
        if (State != MeasureState.Armed || _game is not { } game || _job is not { } job) return;
        var counted = CountdownUntilUtc is not null;
        CountdownUntilUtc = null;
        var process = FindGame(game);
        if (process is null)
        {
            _ = Task.Run(() => Beep(330, 300));
            Set(MeasureState.Armed, $"{game.Name} wasn't running when {(counted ? "the countdown ended" : $"you pressed {HotkeyName}")}. Start it, get to a normal " +
                                    $"gameplay area, then press {HotkeyName} or Start in {CountdownSeconds} seconds again.", true);
            return;
        }

        _hotkey?.Dispose();
        _hotkey = null;
        AppServices.History.Launched(game.Id);
        var settings = _snapshot?.Invoke() ?? new MeasuredSettings();
        var refresh = Helpers.Display.Pick(Helpers.Display.All(), AppServices.Settings.Current.FrameCapDisplay)?.RefreshHz;
        MeasuringSinceUtc = DateTime.UtcNow;
        Set(MeasureState.Measuring, $"Measuring {game.Name} for {Length(_seconds)}… keep playing. Press {HotkeyName} again (or Stop and keep result here) to stop early.", false);
        _ = Task.Run(() => { Beep(880, 120); Beep(1320, 160); });

        // The same key now stops the run early.
        _hotkey = GlobalHotkey.TryRegister(AppServices.Settings.Current.MeasureHotkey);
        if (_hotkey is not null) _hotkey.Pressed += () => _ui?.TryEnqueue(StopEarly);
        StartOverlay(game, process, job.CsvFile, _seconds);

        // Windows' own counters (GPU and CPU usage, video memory) for the same stretch. A missing counter only means a dash.
        using var sampler = SystemSampler.Start();

        MeasureRunResult? result;
        var seconds = _seconds;
        if (_directWorkDir is not null)
        {
            _stopCts = new CancellationTokenSource();
            var stop = _stopCts.Token;
            result = await Task.Run(() => PresentMonRunner.Run(job.PresentMonPath, process.Id, job.CsvFile, seconds, CancellationToken.None, stop));
        }
        else
        {
            await File.WriteAllTextAsync(job.StartFile, process.Id.ToString());
            result = await WaitForResultAsync(job, TimeSpan.FromSeconds(seconds + 45));
        }
        _hotkey?.Dispose();
        _hotkey = null;
        StopOverlay();
        MeasuringSinceUtc = null;
        var gpu = AppServices.Gpu;
        var load = sampler.Stop(gpu is null ? null : VideoMemory.TotalBytes(gpu.VendorId, gpu.DeviceId));
        _ = Task.Run(() => { Beep(1320, 120); Beep(880, 120); Beep(660, 220); });

        var gameClosed = process.HasExited;
        var stats = await Task.Run(() => PresentMonCsv.Read(job.CsvFile, process.Id));
        DeleteWorkDir(TakeWorkDir());   // PresentMon has quit; its CSV and the job files go

        if (stats is null)
        {
            Finish(Why(result, gameClosed), problem: true);
            return;
        }
        var stoppedEarly = result?.StoppedEarly == true;
        var timing = stats.Timing;
        var frameGenOn = stats.GeneratedFrames > 0 || settings.OptiScalerFrameGenOn;
        var verdict = BottleneckRules.Judge(stats.AverageFps, timing, load, CapOf(settings), refresh, frameGenOn);
        AppServices.Measurements.Add(game.Id, new MeasurementRun
        {
            Utc = DateTime.UtcNow, AverageFps = stats.AverageFps, LowFps = stats.LowFps, MaxFps = stats.MaxFps, Frames = stats.Frames, Spikes = stats.Spikes,
            Seconds = stats.Seconds, PlannedSeconds = seconds, StoppedEarly = stoppedEarly,
            GeneratedFrames = stats.GeneratedFrames, RefreshHz = refresh, Settings = settings,
            CpuBusyMs = timing?.CpuBusyMs, CpuWaitMs = timing?.CpuWaitMs, GpuBusyMs = timing?.GpuBusyMs, GpuWaitMs = timing?.GpuWaitMs,
            FrameMs = timing?.FrameMs, GpuPercent = load.GpuPercent, CpuPercent = load.CpuPercent, BusiestCorePercent = load.BusiestCorePercent,
            PeakVramBytes = load.PeakVramBytes, TotalVramBytes = load.TotalVramBytes,
            Verdict = verdict?.Verdict, VerdictLessCertain = verdict?.LessCertain == true, VerdictByVSync = verdict?.ByVSync == true
        });
        Finish($"Measured {game.Name} for {Length(stats.Seconds)}: {Math.Round(stats.AverageFps)} fps average, low points {Math.Round(stats.LowFps)} fps" +
               (stats.MaxFps is { } max ? $", max {Math.Round(max)} fps." : ".") +
               (verdict is { } b ? $" {b.Title}{(b.LessCertain ? " (less certain)" : "")}." : "") +
               (stoppedEarly ? $" You stopped it early (of {Length(seconds)})." : "") +
               (gameClosed && !stoppedEarly ? $" The game closed before the end, so this covers less than {Length(seconds)}." : ""), problem: false);
    }

    /// <summary>Upshift's frame cap from the recorded settings ("60 fps"), or null for "No cap".</summary>
    private static double? CapOf(MeasuredSettings settings) =>
        double.TryParse(settings.FrameCap.Replace("fps", "").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap > 0 ? cap : null;

    /// <summary>A plain reason for an empty measurement.</summary>
    private static string Why(MeasureRunResult? result, bool gameClosed)
    {
        var errors = result?.ErrorText ?? "";
        if (result is null) return "The measuring helper didn't answer, so nothing was recorded. It may have been closed. Press Measure this game to try again.";
        if (result.Problem is { } problem) return problem;
        if (errors.Contains("access", StringComparison.OrdinalIgnoreCase) || errors.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            || errors.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
            return "Windows permission was refused, so PresentMon couldn't read the game's frame timings.";
        if (gameClosed) return "The game closed during the measurement, so nothing was recorded.";
        return "No frames were recorded. The game may have been minimised or paused, or it draws in a way PresentMon can't see." +
               (errors.Length > 0 ? $" PresentMon said: {errors.Split('\n').Last()}" : "");
    }

    private static async Task<MeasureRunResult?> WaitForResultAsync(MeasureJob job, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(job.ResultFile))
            {
                await Task.Delay(200);
                try { return JsonSerializer.Deserialize<MeasureRunResult>(await File.ReadAllTextAsync(job.ResultFile)); }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            await Task.Delay(500);
        }
        try { File.WriteAllText(job.CancelFile, "cancel"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The game's running process: its main exe by name (the one with a window when there are several).</summary>
    private static Process? FindGame(GameInfo game)
    {
        var names = new[] { game.ExePath, game.LaunchExePath }.Where(p => p is not null)
            .Select(p => Path.GetFileNameWithoutExtension(p!)).Distinct(StringComparer.OrdinalIgnoreCase);
        var found = names.SelectMany(n => Process.GetProcessesByName(n)).Where(p => { try { return !p.HasExited; } catch { return false; } }).ToList();
        return found.OrderByDescending(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }).FirstOrDefault();
    }

    /// <summary>OptiScaler's keys in this game's OptiScaler.ini (its defaults when they're left at auto).</summary>
    public static List<(int Vk, string Name)> OptiScalerKeys(GameInfo game)
    {
        var keys = new List<(int, string)> { (0x2D, "menu"), (0x21, "FPS counter"), (0x22, "FPS counter style"), (0x23, "frame generation"), (0x24, "DLSS 5") };
        if (game.TargetDir is { } dir && File.Exists(Path.Combine(dir, "OptiScaler.ini")))
        {
            var ini = IniFile.Load(Path.Combine(dir, "OptiScaler.ini"));
            foreach (var (section, key, name) in new[] { ("Menu", "ShortcutKey", "menu"), ("Menu", "FpsShortcutKey", "FPS counter"),
                         ("Menu", "FpsCycleShortcutKey", "FPS counter style"), ("Menu", "FGShortcutKey", "frame generation"), ("DlssNr", "ToggleKey", "DLSS 5") })
                if (ini.Get(section, key) is { } value && ParseKey(value) is { } vk) keys.Add((vk, name));
        }
        return keys;
    }

    private static int? ParseKey(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex)) return hex;
        return int.TryParse(value, out var dec) && dec > 0 ? dec : null;
    }

    /// <summary>The current measurement's folder, forgotten so a new measurement starts clean.</summary>
    private static string? TakeWorkDir()
    {
        var dir = _job?.WorkDir;
        _job = null;
        _directWorkDir = null;
        return dir;
    }

    private static void DeleteWorkDir(string? dir)
    {
        if (dir is null) return;
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(400); }
        }
    }

    // ---------------- The overlay ----------------

    /// <summary>
    /// Shows the overlay on the game's screen, when "Show overlay while measuring" is on, and updates it four times a
    /// second from the CSV PresentMon is writing. A game in exclusive fullscreen gets no overlay (it couldn't show), and
    /// that's said once per game; measuring goes on either way.
    /// </summary>
    private static void StartOverlay(GameInfo game, Process process, string csv, int seconds)
    {
        StopOverlay();
        var settings = AppServices.Settings.Current;
        if (!settings.MeasureOverlay) return;
        var cts = _overlayCts = new CancellationTokenSource();
        IntPtr window;
        try { window = process.MainWindowHandle; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { window = IntPtr.Zero; }
        var corner = MeasureOverlay.ParseCorner(settings.OverlayCorner);
        _overlayKey = GlobalHotkey.TryRegister(settings.MeasureHotkey, shift: true);
        if (_overlayKey is not null) _overlayKey.Pressed += () => { if (_overlay is { } o) o.Visible = !o.Visible; };
        var since = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var live = new LiveFrames(csv, process.Id);
            var exclusive = MeasureOverlay.ExclusiveFullscreenNow();
            var overlay = exclusive ? null : MeasureOverlay.Show(window, corner);
            _overlay = overlay;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var snap = live.Poll();
                    if (!exclusive && (snap.ExclusiveFullscreen || MeasureOverlay.ExclusiveFullscreenNow()))
                    {
                        exclusive = true;
                        overlay?.Dispose();
                        _overlay = overlay = null;
                    }
                    if (exclusive) NoticeOnce(game);
                    var left = Math.Max(0, seconds - (int)(DateTime.UtcNow - since).TotalSeconds);
                    overlay?.Update(State == MeasureState.Measuring && Message.StartsWith("Stopping", StringComparison.Ordinal) ? "Stopping…" : $"{left / 60}:{left % 60:00} left",
                        snap.Fps is { } fps ? $"FPS {Math.Round(fps)}" + (snap.LowFps is { } low ? $"  ·  1% low {Math.Round(low)}" : "") : "FPS –",
                        snap.FrameMs is { } ms ? $"Frame time {ms:0.0} ms" : "Waiting for frames…");
                    await Task.Delay(250, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally { overlay?.Dispose(); }
        });
    }

    private static void StopOverlay()
    {
        _overlayCts?.Cancel();
        _overlayCts = null;
        _overlayKey?.Dispose();
        _overlayKey = null;
        _overlay?.Dispose();
        _overlay = null;
    }

    /// <summary>The exclusive-fullscreen sentence, once per game (remembered in settings.json).</summary>
    private static void NoticeOnce(GameInfo game)
    {
        var settings = AppServices.Settings.Current;
        if (settings.OverlayNoticeShown.Contains(game.Id)) return;
        settings.OverlayNoticeShown.Add(game.Id);
        AppServices.Settings.Save(settings);
        OverlayNotice = ExclusiveFullscreenText;
        OverlayNoticeGameId = game.Id;
        _ui?.TryEnqueue(() => Changed?.Invoke());
    }

    private static void Finish(string message, bool problem)
    {
        StopOverlay();
        _hotkey?.Dispose();
        _hotkey = null;
        MeasuringSinceUtc = null;
        CountdownUntilUtc = null;
        Set(MeasureState.Idle, message, problem);
        _game = null;
    }

    private static void Set(MeasureState state, string message, bool problem)
    {
        State = state;
        Message = message;
        MessageGameId = _game?.Id ?? MessageGameId;
        MessageIsProblem = problem;
        if (_ui is { } ui && !ui.HasThreadAccess) ui.TryEnqueue(() => Changed?.Invoke());
        else Changed?.Invoke();
    }

    [DllImport("kernel32.dll")] private static extern bool Beep(uint frequency, uint durationMs);
}
