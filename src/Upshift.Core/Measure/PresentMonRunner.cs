using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Upshift.Core.Install;

namespace Upshift.Core.Measure;

/// <summary>
/// What the measuring helper does (Upshift.exe --measure job.json, started with Windows' permission prompt): wait for
/// the signal to start, run PresentMon once for one process, then quit. All paths are inside Upshift's data folder.
/// </summary>
public sealed class MeasureJob
{
    /// <summary>PresentMon's exe, in Upshift's components folder.</summary>
    public string PresentMonPath { get; set; } = "";
    /// <summary>The folder for this measurement (start signal, cancel signal, CSV, result).</summary>
    public string WorkDir { get; set; } = "";
    public int Seconds { get; set; } = MeasureThresholds.MeasureSeconds;
    /// <summary>The main Upshift window's process: the helper quits when it's gone.</summary>
    public int ParentProcessId { get; set; }
    /// <summary>The helper gives up waiting after this.</summary>
    public DateTime DeadlineUtc { get; set; }

    public string StartFile => Path.Combine(WorkDir, "start.txt");     // contains the game's process id
    public string CancelFile => Path.Combine(WorkDir, "cancel.txt");
    public string CsvFile => Path.Combine(WorkDir, "frames.csv");
    public string ResultFile => Path.Combine(WorkDir, "result.json");
}

/// <summary>How a PresentMon run ended.</summary>
public sealed class MeasureRunResult
{
    public bool Started { get; set; }
    public int ExitCode { get; set; }
    /// <summary>The last lines PresentMon wrote to its error output (to explain a failure).</summary>
    public string ErrorText { get; set; } = "";
    public bool TimedOut { get; set; }
    public string? Problem { get; set; }
}

public static class PresentMonRunner
{
    public static readonly string[] IntelSigners = { "Intel Corporation" };
    private const string SessionName = "UpshiftMeasure";

    /// <summary>
    /// PresentMon can read frame timings without a prompt when Upshift already runs elevated or the user is in the
    /// Windows "Performance Log Users" group (S-1-5-32-559).
    /// </summary>
    public static bool CanCaptureWithoutPrompt()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            // Under UAC the Administrators role only counts in an elevated process.
            return principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(new SecurityIdentifier("S-1-5-32-559"));
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Records <paramref name="seconds"/> of frames for one process into a CSV and waits for PresentMon to quit. It is
    /// stopped if it overruns, so it never keeps running in the background.
    /// </summary>
    public static MeasureRunResult Run(string presentMon, int processId, string csv, int seconds, CancellationToken ct)
    {
        var result = new MeasureRunResult();
        var info = new ProcessStartInfo
        {
            FileName = presentMon,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(csv)!
        };
        foreach (var arg in new[]
                 {
                     "--process_id", processId.ToString(), "--output_file", csv, "--timed", seconds.ToString(), "--terminate_after_timed",
                     "--no_console_stats", "--track_frame_type", "--session_name", SessionName, "--stop_existing_session"
                 })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info);
        if (process is null) { result.Problem = "PresentMon didn't start."; return result; }
        result.Started = true;
        var errors = new List<string>();
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) lock (errors) errors.Add(line); };
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) lock (errors) errors.Add(line); };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        bool finished;
        try { finished = process.WaitForExitAsync(ct).Wait(TimeSpan.FromSeconds(seconds + 20)); }
        catch (AggregateException) { finished = false; }   // cancelled
        if (!finished)
        {
            result.TimedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            StopSession(presentMon);
        }
        process.WaitForExit(5000);
        result.ExitCode = process.HasExited ? process.ExitCode : -1;
        lock (errors) result.ErrorText = string.Join("\n", errors.TakeLast(8));
        return result;
    }

    /// <summary>Ends Upshift's PresentMon trace session if one is still open (after a forced stop).</summary>
    private static void StopSession(string presentMon)
    {
        try
        {
            using var stop = Process.Start(new ProcessStartInfo(presentMon)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--terminate_existing_session", "--session_name", SessionName }
            });
            stop?.WaitForExit(5000);
        }
        catch (Exception) { }
    }

    // ---------------- the helper side ----------------

    /// <summary>
    /// Upshift.exe --measure job.json --sha256 HASH: checks the job wasn't changed and only runs an Intel-signed
    /// PresentMon from Upshift's components folder, waits for the start signal (or cancel, the main window closing, or
    /// the deadline), runs one capture and writes the result. Returns the process exit code.
    /// </summary>
    public static int RunHelper(string jobPath, string expectedHash, string dataDir)
    {
        if (!File.Exists(jobPath) || OptiScalerInstaller.Sha256(jobPath) != expectedHash) return 2;
        var job = JsonSerializer.Deserialize<MeasureJob>(File.ReadAllText(jobPath));
        if (job is null) return 2;
        var result = new MeasureRunResult();
        try
        {
            var components = Path.GetFullPath(Path.Combine(dataDir, "components")) + Path.DirectorySeparatorChar;
            var work = Path.GetFullPath(Path.Combine(dataDir, "measure")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(job.PresentMonPath).StartsWith(components, StringComparison.OrdinalIgnoreCase)
                || !(Path.GetFullPath(job.WorkDir) + Path.DirectorySeparatorChar).StartsWith(work, StringComparison.OrdinalIgnoreCase))
                result.Problem = "The measuring job points outside Upshift's folders, so nothing was run.";
            else if (SignatureCheck.Problem(job.PresentMonPath, IntelSigners) is { } signature)
                result.Problem = "PresentMon isn't signed by Intel, so it wasn't run. " + signature;
            else
            {
                // Wait for the hotkey (the main window writes the game's process id), a cancel, or the main window to close.
                while (true)
                {
                    if (File.Exists(job.CancelFile) || DateTime.UtcNow > job.DeadlineUtc || !IsRunning(job.ParentProcessId)) return 0;
                    if (File.Exists(job.StartFile) && int.TryParse(File.ReadAllText(job.StartFile).Trim(), out var pid))
                    {
                        using var cancel = new CancellationTokenSource();
                        var watch = Task.Run(async () =>
                        {
                            while (!cancel.IsCancellationRequested)
                            {
                                if (File.Exists(job.CancelFile) || !IsRunning(job.ParentProcessId)) { cancel.Cancel(); break; }
                                await Task.Delay(250);
                            }
                        });
                        result = Run(job.PresentMonPath, pid, job.CsvFile, job.Seconds, cancel.Token);
                        cancel.Cancel();
                        break;
                    }
                    Thread.Sleep(150);
                }
            }
        }
        catch (Exception ex) { result.Problem = ex.Message; }
        File.WriteAllText(job.ResultFile, JsonSerializer.Serialize(result));
        return result.Problem is null ? 0 : 1;
    }

    private static bool IsRunning(int processId)
    {
        try { return !Process.GetProcessById(processId).HasExited; }
        catch (Exception) { return false; }
    }
}
