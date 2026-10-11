using System.Diagnostics;
using System.Globalization;

namespace Upshift.Core.ShaderPrebuild;

/// <summary>Progress of a pre-build: pipelines done, of how many, and how many failed.</summary>
public sealed record PrebuildProgress(int Done, int Total, int Failed);

/// <summary>How a pre-build ended: built and failed counts, its time, the card it ran on; or why it didn't run.</summary>
/// <param name="Different">Pipelines rebuilt from a description that didn't hash back to their record (a bug; should be 0).</param>
public sealed record PrebuildResult(bool Success, int Built, int Failed, double Seconds, string? Card, string? Problem, int Different = 0);

/// <summary>Runs upshift_prebuild.exe for one game (never elevated, no window) and reads its progress lines.</summary>
public static class PrebuildRunner
{
    public static async Task<PrebuildResult> RunAsync(string prebuildExe, string program, string gameDir, IProgress<PrebuildProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(prebuildExe)) return new(false, 0, 0, 0, null, "Upshift's pre-build program (ShaderPrebuild\\upshift_prebuild.exe) is missing from its program folder.");
        var start = new ProcessStartInfo(prebuildExe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(prebuildExe)!
        };
        start.ArgumentList.Add("--prebuild");
        start.ArgumentList.Add(program);
        start.ArgumentList.Add("--dir");
        start.ArgumentList.Add(gameDir);

        string? card = null, problem = null;
        int built = 0, failed = 0, different = 0;
        double seconds = 0;
        var gotResult = false;
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new(false, 0, 0, 0, null, "Windows didn't start the pre-build program.");
            using var cancel = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            _ = process.StandardError.ReadToEndAsync(ct);
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var (head, rest) = line.IndexOf(' ') is var i and > 0 ? (line[..i], line[(i + 1)..]) : (line, "");
                var f = rest.Split(' ');
                switch (head)
                {
                    case "card": card = rest.Split('\t')[0]; break;
                    case "progress" when f.Length >= 3 && int.TryParse(f[0], out var d) && int.TryParse(f[1], out var t) && int.TryParse(f[2], out var fl):
                        progress?.Report(new PrebuildProgress(d, t, fl));
                        break;
                    case "result" when f.Length >= 3:
                        int.TryParse(f[0], out built);
                        int.TryParse(f[1], out failed);
                        double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                        if (f.Length >= 5) int.TryParse(f[4], out different);
                        gotResult = true;
                        break;
                    case "error": problem = rest; break;
                }
            }
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { return new(false, built, failed, seconds, card, "The pre-build was stopped."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new(false, 0, 0, 0, card, $"The pre-build program couldn't run: {ex.Message}");
        }
        if (problem is not null) return new(false, built, failed, seconds, card, problem);
        if (!gotResult) return new(false, built, failed, seconds, card, "The pre-build program stopped without saying how it went.");
        return new(built > 0, built, failed, seconds, card, built > 0 ? null : "Nothing could be built: the driver refused every pipeline.", different);
    }
}
