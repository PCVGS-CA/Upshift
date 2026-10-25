using System.Diagnostics;
using Upshift.Core.Measure;
using Upshift.Core.Models;

namespace Upshift.App.Services;

public enum OverlayState { Off, Preparing, WaitingForGame, Running }

/// <summary>
/// "Show overlay" on the Performance tab: the measuring overlay without measuring, for any game, with or without
/// OptiScaler. PresentMon (the same download and the same Windows permission step as Measure) captures the game's
/// process in the background and feeds <see cref="MeasureOverlay"/> through <see cref="LiveFrames"/>. Nothing is saved:
/// the capture's folder is deleted when it stops, which is when the switch goes off or the game closes. A measurement
/// of the same game takes over this capture (<see cref="BeginRecording"/>) instead of starting a second PresentMon.
/// Changed is raised on the UI thread.
/// </summary>
public static class LiveOverlay
{
    private static GameInfo? _game;
    private static MeasureJob? _job;
    private static bool _direct;
    private static string? _presentMon;
    private static bool _captureStarted;
    private static CancellationTokenSource? _cts;
    private static CancellationTokenSource? _stopCapture;
    private static MeasureOverlay? _overlay;
    private static GlobalHotkey? _key;

    // A measurement taking over the capture (set and read under _sync).
    private static readonly object _sync = new();
    private static LiveFrames? _recordFrames;
    private static DateTime _recordUntilUtc;

    public static Microsoft.UI.Dispatching.DispatcherQueue? Ui { get; private set; }
    public static OverlayState State { get; private set; }
    public static string? GameId => _game?.Id;
    /// <summary>The captured game's process id while running.</summary>
    public static int? ProcessId { get; private set; }
    /// <summary>What's happening, or why it stopped (for the game it's about).</summary>
    public static string Message { get; private set; } = "";
    public static string? MessageGameId { get; private set; }
    public static event Action? Changed;

    public static bool ActiveFor(string gameId) => State != OverlayState.Off && _game?.Id == gameId;
    public static bool RunningFor(string gameId) => State == OverlayState.Running && _game?.Id == gameId;

    /// <summary>Turns the overlay on for this game: PresentMon, the permission step, then waits for the game to run.</summary>
    public static async Task StartAsync(GameInfo game)
    {
        Stop(silent: true);
        Ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (Measurement.State != MeasureState.Idle)
        {
            Set(OverlayState.Off, "A measurement is running; turn the overlay on when it's done.", game);
            return;
        }
        if (game.ExePath is null && game.LaunchExePath is null)
        {
            Set(OverlayState.Off, $"Upshift doesn't know which program is {game.Name}'s, so it can't find it running.", game);
            return;
        }
        _game = game;
        var cts = _cts = new CancellationTokenSource();
        Set(OverlayState.Preparing, "Getting PresentMon ready…", game);

        var (presentMon, problem) = await Measurement.PresentMonAsync(s => Set(OverlayState.Preparing, $"Getting PresentMon: {s}", game), cts.Token);
        if (cts.IsCancellationRequested) return;
        if (presentMon is null)
        {
            Finish(problem ?? "The overlay was turned off.");
            return;
        }
        var work = Path.Combine(AppServices.DataDir, "measure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        _job = new MeasureJob
        {
            PresentMonPath = presentMon, WorkDir = work, Live = true, ParentProcessId = Environment.ProcessId,
            DeadlineUtc = DateTime.UtcNow.AddMinutes(30)
        };
        _direct = PresentMonRunner.CanCaptureWithoutPrompt() || Measurement.TestPresentMon is not null;
        _presentMon = presentMon;
        if (!_direct && !await Measurement.StartHelperAsync(_job, cts.Token))
        {
            Finish("Windows permission was refused, so the overlay can't read the game's frames.");
            return;
        }
        Set(OverlayState.WaitingForGame, $"Start {game.Name}; the overlay appears when it runs.", game);
        _ = Task.Run(() => LoopAsync(game, _job, presentMon, cts.Token));
    }

    /// <summary>Turns the overlay off: PresentMon stops and its folder (the capture) is deleted.</summary>
    public static void Stop(bool silent = false)
    {
        var wasOn = State != OverlayState.Off;
        _cts?.Cancel();
        _cts = null;
        _stopCapture?.Cancel();
        _stopCapture = null;
        // A direct capture's PresentMon is a separate program: end its session now, so it never outlives the switch or
        // Upshift itself (the helper's watches for the cancel signal and for Upshift closing).
        if (_direct && _captureStarted && _presentMon is { } pm)
            try { PresentMonRunner.StopLiveSession(pm); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _captureStarted = false;
        _key?.Dispose();
        _key = null;
        _overlay?.Dispose();
        _overlay = null;
        lock (_sync) _recordFrames = null;
        if (_job is { } job)
        {
            try { File.WriteAllText(job.CancelFile, "cancel"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var dir = job.WorkDir;
            _ = Task.Run(async () => { await Task.Delay(2000); DeleteFolder(dir); });   // after PresentMon has let go of the CSV
        }
        _job = null;
        ProcessId = null;
        if (wasOn && !silent) Set(OverlayState.Off, "Overlay off.", _game);
        else State = OverlayState.Off;
        _game = null;
    }

    private static void Finish(string message)
    {
        var game = _game;
        Stop(silent: true);
        Set(OverlayState.Off, message, game);
    }

    private static async Task LoopAsync(GameInfo game, MeasureJob job, string presentMon, CancellationToken ct)
    {
        Process? process = null;
        LiveFrames? live = null;
        var exclusive = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (process is null)
                {
                    if (DateTime.UtcNow > job.DeadlineUtc) { Ui?.TryEnqueue(() => Finish($"{game.Name} didn't start within 30 minutes, so the overlay was turned off.")); return; }
                    if (Measurement.FindGame(game) is { } found)
                    {
                        process = found;
                        ProcessId = found.Id;
                        if (_direct)
                        {
                            var stop = _stopCapture = new CancellationTokenSource();
                            _captureStarted = true;
                            _ = Task.Run(() => PresentMonRunner.Run(presentMon, found.Id, job.CsvFile, 0, ct, stop.Token, live: true));
                        }
                        else await File.WriteAllTextAsync(job.StartFile, found.Id.ToString(), ct);
                        live = new LiveFrames(job.CsvFile, found.Id);
                        exclusive = MeasureOverlay.ExclusiveFullscreenNow();
                        if (!exclusive) _overlay = MeasureOverlay.Show(Window(found), MeasureOverlay.ParseCorner(AppServices.Settings.Current.OverlayCorner));
                        _key = GlobalHotkey.TryRegister(AppServices.Settings.Current.MeasureHotkey, shift: true);
                        if (_key is not null) _key.Pressed += () => { if (_overlay is { } o) o.Visible = !o.Visible; };
                        Ui?.TryEnqueue(() => Set(OverlayState.Running, $"Showing the overlay for {game.Name}. {Measurement.OverlayKeyName} hides it.", game));
                    }
                }
                else
                {
                    if (process.HasExited) { Ui?.TryEnqueue(() => Finish($"{game.Name} closed, so the overlay stopped.")); return; }
                    LiveFrames? recording;
                    DateTime until;
                    lock (_sync) (recording, until) = (_recordFrames, _recordUntilUtc);
                    var snap = (recording ?? live!).Poll();
                    if (recording is not null) live!.Poll();   // keep the whole-session figures current too
                    if (!exclusive && (snap.ExclusiveFullscreen || MeasureOverlay.ExclusiveFullscreenNow()))
                    {
                        exclusive = true;
                        _overlay?.Dispose();
                        _overlay = null;
                    }
                    if (exclusive) Ui?.TryEnqueue(() => Measurement.NoticeOnce(game));
                    if (_overlay is { } overlay)
                    {
                        overlay.Follow(Window(process));
                        var left = Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                        overlay.Update(recording is null ? "Live" : $"{left / 60}:{left % 60:00} left",
                            snap.Fps is { } fps ? $"FPS {Math.Round(fps)}" + (snap.LowFps is { } low ? $"  ·  1% low {Math.Round(low)}" : "") : "FPS –",
                            snap.FrameMs is { } ms ? $"Frame time {ms:0.0} ms" : "Waiting for frames…", snap.Graph, recording: recording is not null);
                    }
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Ui?.TryEnqueue(() => Finish($"The overlay stopped: {ex.Message}"));
        }
    }

    // ---------------- A measurement taking over ----------------

    /// <summary>
    /// A measurement of this game starts: the overlay shows the recording (dot, time left, figures for the recording only)
    /// and the caller gets the point of the capture where it starts. Null when the overlay isn't capturing this game.
    /// </summary>
    public static (string Csv, long Offset)? BeginRecording(string gameId, int seconds)
    {
        if (!RunningFor(gameId) || _job is not { } job || ProcessId is not { } pid) return null;
        long offset;
        try { offset = File.Exists(job.CsvFile) ? new FileInfo(job.CsvFile).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        lock (_sync) (_recordFrames, _recordUntilUtc) = (new LiveFrames(job.CsvFile, pid, offset), DateTime.UtcNow.AddSeconds(seconds));
        return (job.CsvFile, offset);
    }

    /// <summary>The measurement is over: back to the plain overlay. Returns where in the capture it ended.</summary>
    public static long? EndRecording()
    {
        lock (_sync) _recordFrames = null;
        try { return _job is { } job && File.Exists(job.CsvFile) ? new FileInfo(job.CsvFile).Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// At start-up: capture folders (data\measure\*) nothing has written to for an hour are left over from an Upshift that
    /// closed during a measurement or with the overlay on. Removed; a running capture writes all the time.
    /// </summary>
    public static void CleanLeftovers()
    {
        try
        {
            var root = Path.Combine(AppServices.DataDir, "measure");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var newest = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc)
                    .Append(Directory.GetLastWriteTimeUtc(dir)).Max();
                if (DateTime.UtcNow - newest > TimeSpan.FromHours(1)) DeleteFolder(dir);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static IntPtr Window(Process process)
    {
        try { process.Refresh(); return process.MainWindowHandle; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return IntPtr.Zero; }
    }

    private static void DeleteFolder(string dir)
    {
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    private static void Set(OverlayState state, string message, GameInfo? game)
    {
        State = state;
        Message = message;
        MessageGameId = game?.Id ?? MessageGameId;
        if (Ui is { } ui && !ui.HasThreadAccess) ui.TryEnqueue(() => Changed?.Invoke());
        else Changed?.Invoke();
    }
}
