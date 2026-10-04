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
/// 2. The hotkey (F10 by default) starts 60 seconds of PresentMon for the game's process only, with a beep at the start
///    and at the end.
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
            _job = new MeasureJob { PresentMonPath = presentMon, WorkDir = work };
        }
        else
        {
            _job = new MeasureJob
            {
                PresentMonPath = presentMon, WorkDir = work, ParentProcessId = Environment.ProcessId,
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

        _hotkey = GlobalHotkey.TryRegister(key);
        if (_hotkey is null)
        {
            Cancel(silent: true);
            Finish($"{HotkeyName} is already used by another program, so it can't start a measurement. Choose another measuring key in Settings.", problem: true);
            return;
        }
        _hotkey.Pressed += () => _ui?.TryEnqueue(async () => await StartAsync());
        Set(MeasureState.Armed,
            $"Ready. Start {game.Name} and get to a normal gameplay area, then press {HotkeyName}. You'll hear a beep when measuring starts " +
            $"and two when it ends ({MeasureThresholds.MeasureSeconds} seconds). Waits up to 30 minutes.", false);
    }

    /// <summary>Stops waiting (or a measurement in progress). The helper sees the cancel signal and quits.</summary>
    public static void Cancel(bool silent = false)
    {
        _armCts?.Cancel();
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
        var process = FindGame(game);
        if (process is null)
        {
            _ = Task.Run(() => Beep(330, 300));
            Set(MeasureState.Armed, $"{game.Name} wasn't running when you pressed {HotkeyName}. Start it, get to a normal gameplay area, then press {HotkeyName} again.", true);
            return;
        }

        _hotkey?.Dispose();
        _hotkey = null;
        var settings = _snapshot?.Invoke() ?? new MeasuredSettings();
        var refresh = Helpers.Display.Pick(Helpers.Display.All(), AppServices.Settings.Current.FrameCapDisplay)?.RefreshHz;
        Set(MeasureState.Measuring, $"Measuring {game.Name} for {MeasureThresholds.MeasureSeconds} seconds… keep playing.", false);
        _ = Task.Run(() => { Beep(880, 120); Beep(1320, 160); });

        MeasureRunResult? result;
        if (_directWorkDir is not null)
            result = await Task.Run(() => PresentMonRunner.Run(job.PresentMonPath, process.Id, job.CsvFile, MeasureThresholds.MeasureSeconds, CancellationToken.None));
        else
        {
            await File.WriteAllTextAsync(job.StartFile, process.Id.ToString());
            result = await WaitForResultAsync(job, TimeSpan.FromSeconds(MeasureThresholds.MeasureSeconds + 45));
        }
        _ = Task.Run(() => { Beep(1320, 120); Beep(880, 120); Beep(660, 220); });

        var gameClosed = process.HasExited;
        var stats = await Task.Run(() => PresentMonCsv.Read(job.CsvFile, process.Id));
        DeleteWorkDir(TakeWorkDir());   // PresentMon has quit; its CSV and the job files go

        if (stats is null)
        {
            Finish(Why(result, gameClosed), problem: true);
            return;
        }
        AppServices.Measurements.Add(game.Id, new MeasurementRun
        {
            Utc = DateTime.UtcNow, AverageFps = stats.AverageFps, LowFps = stats.LowFps, Frames = stats.Frames, Seconds = stats.Seconds,
            GeneratedFrames = stats.GeneratedFrames, RefreshHz = refresh, Settings = settings
        });
        Finish($"Measured {game.Name}: {Math.Round(stats.AverageFps)} fps average, low points {Math.Round(stats.LowFps)} fps." +
               (gameClosed ? " The game closed before the end, so this covers less than a minute." : ""), problem: false);
    }

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

    private static void Finish(string message, bool problem)
    {
        _hotkey?.Dispose();
        _hotkey = null;
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
