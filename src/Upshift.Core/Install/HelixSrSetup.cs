using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upshift.Core.Catalog;

namespace Upshift.Core.Install;

/// <summary>One step of the setup: a plain-language step name, a rough 0-1 fraction, and the script's own line.</summary>
public sealed record HelixSrProgress(string Step, double Fraction, string? Line = null);

/// <summary>The setup's outcome. Message is the real error when it failed; Output is the end of the script's output.</summary>
public sealed record HelixSrSetupResult(bool Success, string Message, string? Output = null, bool MaybeBlocked = false);

/// <summary>
/// Sets HelixSR up in Upshift's shared folder by running the HelixSR author's own setup, once:
/// 1. downloads the one release the catalog names, from its GitHub release, and checks its size and SHA-256;
/// 2. unpacks it into a temporary folder and checks every file Upshift will run against the catalog's SHA-256 (the
///    setup scripts, the author's helper program and the DLL): nothing runs when one doesn't match;
/// 3. runs helixsr-setup.ps1 with an output folder and -Yes (its documented switch for "the user agreed to download
///    NVIDIA's DLSS file"; Upshift's notice asked first). The script downloads its tools into %LOCALAPPDATA%\HelixSR,
///    downloads NVIDIA's DLSS 310.7.0 DLL from NVIDIA's GitHub, builds helixsr_weights.bin and helixsr_kernels.pak, and
///    deletes NVIDIA's DLL;
/// 4. puts HelixSR's DLL next to them under both names its README's OptiScaler section asks for;
/// 5. only then moves the finished folder into place. Any failure deletes the temporary folder, so nothing is left
///    half-installed, and no game is changed by the setup at all.
/// </summary>
public static class HelixSrSetup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool Running => Gate.CurrentCount == 0;

    public static async Task<HelixSrSetupResult> RunAsync(HelixSrRelease release, string dataDir, HttpClient http,
        IProgress<HelixSrProgress>? progress, CancellationToken cancel, string? powershell = null)
    {
        if (!await Gate.WaitAsync(0, cancel)) return new HelixSrSetupResult(false, "The HelixSR setup is already running.");
        var root = HelixSr.Root(dataDir);
        var staging = Path.Combine(root, "setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            Directory.CreateDirectory(staging);
            var output = Path.Combine(staging, "files");
            Directory.CreateDirectory(output);

            // ---- 1. the release ----
            progress?.Report(new HelixSrProgress($"Downloading HelixSR {release.Version} from its GitHub release", 0.01));
            var zip = Path.Combine(staging, "HelixSR.zip");
            await DownloadAsync(http, release.AssetUrl, zip, release.AssetSize, cancel);
            var zipHash = Sha256(zip);
            if (!zipHash.Equals(release.AssetSha256, StringComparison.OrdinalIgnoreCase))
                return Fail($"The downloaded HelixSR {release.Version} isn't the release Upshift checked (its SHA-256 is different), so nothing was run.");

            // ---- 2. unpack and check everything that will run ----
            var unpacked = Path.Combine(staging, "release");
            ZipFile.ExtractToDirectory(zip, unpacked);   // refuses entries that would land outside the folder
            var folder = Path.Combine(unpacked, release.ZipFolder);
            foreach (var (relative, sha) in release.ScriptSha256)
            {
                var path = Path.Combine(folder, relative.Replace('/', '\\'));
                if (!File.Exists(path)) return Fail($"HelixSR's {relative} is missing from the release, so nothing was run.");
                if (!Sha256(path).Equals(sha, StringComparison.OrdinalIgnoreCase))
                    return Fail($"HelixSR's {relative} isn't the copy Upshift checked (its SHA-256 is different), so nothing was run.");
            }
            var script = Path.Combine(folder, release.SetupScript);
            if (!release.ScriptSha256.ContainsKey(release.SetupScript))
                return Fail("The catalog has no hash for HelixSR's setup script, so it wasn't run.");

            // ---- 3. the author's setup ----
            progress?.Report(new HelixSrProgress("Starting HelixSR's setup", 0.05));
            var (exit, tail, started) = await RunScriptAsync(powershell ?? "powershell.exe", script, output, folder, progress, cancel);
            if (!started) return new HelixSrSetupResult(false, $"Windows didn't start HelixSR's setup: {tail}", tail, MaybeBlocked: true);
            if (exit != 0)
            {
                var error = tail.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
                            ?? tail.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"exit code {exit}";
                var blocked = tail.Contains("Application Control", StringComparison.OrdinalIgnoreCase) || tail.Contains("blocked", StringComparison.OrdinalIgnoreCase)
                              || tail.Contains("0xc0e90002", StringComparison.OrdinalIgnoreCase) || tail.Contains("not recognized as", StringComparison.OrdinalIgnoreCase);
                return new HelixSrSetupResult(false, $"HelixSR's setup stopped: {error}", tail, blocked);
            }

            // ---- 4. the finished folder ----
            foreach (var built in new[] { HelixSr.WeightsName, HelixSr.KernelsName })
                if (!File.Exists(Path.Combine(output, built)) || new FileInfo(Path.Combine(output, built)).Length == 0)
                    return Fail($"HelixSR's setup finished without making {built}.", tail);
            File.Copy(Path.Combine(folder, HelixSr.DllName), Path.Combine(output, HelixSr.DllName));
            File.Copy(Path.Combine(folder, HelixSr.DllName), Path.Combine(output, HelixSr.UpscalerDllName));
            foreach (var extra in new[] { "helixsr.ini", "LICENSE", "THIRD_PARTY_NOTICES.md", "README.md" })
                if (File.Exists(Path.Combine(folder, extra))) File.Copy(Path.Combine(folder, extra), Path.Combine(output, extra));
            File.WriteAllText(Path.Combine(output, HelixSr.MarkerName), JsonSerializer.Serialize(new HelixSrInstalled
            {
                Version = release.Version, SetUpUtc = DateTime.UtcNow, AssetSha256 = zipHash
            }, new JsonSerializerOptions { WriteIndented = true }));

            // ---- 5. into place (a previous setup is replaced only once this one is complete) ----
            progress?.Report(new HelixSrProgress("Finishing", 0.98));
            var files = HelixSr.FilesDir(dataDir);
            string? old = null;
            if (Directory.Exists(files))
            {
                old = Path.Combine(root, "old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.Move(files, old);
            }
            try { Directory.Move(output, files); }
            catch
            {
                if (old is not null) Directory.Move(old, files);
                throw;
            }
            if (old is not null) TryDelete(old);
            progress?.Report(new HelixSrProgress("Done", 1));
            return new HelixSrSetupResult(true, $"HelixSR {release.Version} is set up.", tail);
        }
        catch (OperationCanceledException) { return Fail("The HelixSR setup was cancelled. Nothing was kept."); }
        catch (HttpRequestException ex) { return Fail($"Downloading HelixSR failed: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail($"The HelixSR setup failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anything else is reported the same way; the setup never takes Upshift down.
            return Fail($"The HelixSR setup failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
            Gate.Release();
        }
    }

    private static HelixSrSetupResult Fail(string message, string? output = null) => new(false, message, output);

    private static async Task DownloadAsync(HttpClient http, string url, string path, long expectedSize, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using (var file = File.Create(path))
            await response.Content.CopyToAsync(file, cancel);
        var size = new FileInfo(path).Length;
        if (expectedSize > 0 && size != expectedSize)
            throw new InvalidDataException($"the download is {size:N0} bytes, not the {expectedSize:N0} the release should be");
    }

    /// <summary>
    /// Runs helixsr-setup.ps1 OUT -Yes in Windows PowerShell (the .bat that wraps it ends with "pause", which would wait
    /// for a key). Standard input is closed, so a question the script might ask gets no answer instead of hanging.
    /// Progress follows the script's own "[helixsr-setup] ..." lines. 45 minutes at most.
    /// </summary>
    private static async Task<(int Exit, string Tail, bool Started)> RunScriptAsync(string powershell, string script, string output, string workDir,
        IProgress<HelixSrProgress>? progress, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Target", output, "-Yes" })
            info.ArgumentList.Add(arg);

        var tail = new Queue<string>();
        void Keep(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 40) tail.Dequeue();
            }
        }

        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("no process"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message, false); }

        using (process)
        {
            process.StandardInput.Close();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                Keep(line);
                if (StepFor(line) is { } step) progress?.Report(step with { Line = line });
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) Keep(line); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (cancel.IsCancellationRequested) throw;
                Keep("Upshift stopped the setup after 45 minutes.");
                return (-1, Joined(), true);
            }
            process.WaitForExit();   // the last output lines
            return (process.ExitCode, Joined(), true);
        }

        string Joined() { lock (tail) return string.Join("\n", tail); }
    }

    /// <summary>The script's progress lines as plain steps (helixsr-setup.ps1 and setup/helixsr_setup.py, 1.4.3).</summary>
    public static HelixSrProgress? StepFor(string line)
    {
        if (!line.StartsWith("[helixsr-setup]", StringComparison.Ordinal)) return null;
        var text = line["[helixsr-setup]".Length..].Trim();
        if (text.StartsWith("downloading python", StringComparison.OrdinalIgnoreCase)) return new("Downloading Python from python.org", 0.08);
        if (text.StartsWith("downloading numpy", StringComparison.OrdinalIgnoreCase)) return new("Downloading numpy from PyPI", 0.11);
        if (text.StartsWith("downloading dxc", StringComparison.OrdinalIgnoreCase)) return new("Downloading Microsoft's shader compiler", 0.14);
        if (text.Contains("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)) return new("Downloading NVIDIA's DLSS file from NVIDIA's GitHub", 0.18);
        if (text.Length > 3 && text[1] == '/' && text[3] == ' ' && char.IsDigit(text[0]) && text[2] == '5')
        {
            var n = text[0] - '0';
            var what = text[4..].Trim();
            return new($"Building the network, step {n} of 5 ({what})", 0.2 + 0.15 * (n - 1));
        }
        if (text.StartsWith("using sm_", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.StartsWith("done", StringComparison.OrdinalIgnoreCase) || text.StartsWith("HelixSR is ready", StringComparison.OrdinalIgnoreCase))
            return new("Checking the result", 0.96);
        return null;
    }

    /// <summary>
    /// Removes Upshift's shared HelixSR folder. With <paramref name="tools"/>, also the setup's own tools folder
    /// (%LOCALAPPDATA%\HelixSR). Returns what couldn't be removed (a game using the files still has them open).
    /// </summary>
    public static List<string> Remove(string dataDir, bool tools)
    {
        var problems = new List<string>();
        void Delete(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{dir}: {ex.Message}"); }
        }
        Delete(HelixSr.Root(dataDir));
        if (tools) Delete(HelixSr.ToolsDir);
        return problems;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
