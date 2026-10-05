namespace Upshift.Core.Install;

public enum LoadState
{
    /// <summary>OptiScaler's log is off for this game, so there's nothing to read.</summary>
    LoggingOff,
    /// <summary>No run since the install (or since logging was turned on).</summary>
    NotRunYet,
    /// <summary>The game ran (Upshift started it) but OptiScaler wrote no log: it didn't load.</summary>
    DidNotLoad,
    /// <summary>OptiScaler loaded but the game never created an upscaler it could take over.</summary>
    LoadedNothingTaken,
    /// <summary>OptiScaler took over the game's upscaler.</summary>
    Working,
    /// <summary>A build whose log format Upshift hasn't checked (AMD-NR).</summary>
    Unsupported
}

/// <summary>What OptiScaler's log says about the last run.</summary>
/// <param name="GameSent">"DLSS", "FSR 3.1", "XeSS"…: what the game asked for.</param>
/// <param name="Used">"FSR 4", "XeSS", "DLSS"…: what OptiScaler ran in its place.</param>
/// <param name="RunLocal">When that run happened (the log's last write), local time.</param>
public sealed record LoadStatus(LoadState State, string? GameSent = null, string? Used = null, DateTime? RunLocal = null, bool FellBack = false);

/// <summary>
/// OptiScaler's own log (OptiScaler.log next to the game's exe), read after the game ran. OptiScaler 0.9.4 doesn't log
/// by default ([Log] LogToFile=false); Upshift turns on the lightest useful level: LogToFile=true, LogLevel=2 (Info).
/// Info logs only events (OptiScaler loading, an upscaler being created or switched), not every frame, and it's written
/// asynchronously (LogAsync's default), so it doesn't slow the game down. The file is replaced each time the game starts.
/// The DLSS 5 build (DLSSNR 0.2.0) writes the same lines; the AMD-NR build hasn't been checked.
/// </summary>
public static class OptiScalerLog
{
    public const string FileName = "OptiScaler.log";

    /// <summary>The ini values that turn the log on (null puts back what the file had).</summary>
    public static List<IniSetting> LogSettings(bool on) => new()
    {
        new("Log", "LogToFile", on ? "true" : null),
        new("Log", "LogLevel", on ? "2" : null)
    };

    public static bool LoggingOn(IniFile ini) =>
        string.Equals(ini.Get("Log", "LogToFile"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The status for a game. <paramref name="lastLaunchUtc"/> is when Upshift last started the game (Play or
    /// Measure), which tells "didn't load" from "not run yet" when there's no log. <paramref name="fsr4"/> says FSR 3.1
    /// requests run as FSR 4 (Upshift's FSR 4 option, or an RX 9000 card).
    /// </summary>
    public static LoadStatus Status(string targetDir, InstallManifest manifest, IniFile ini, DateTime? lastLaunchUtc, bool fsr4)
    {
        if (manifest.ComponentId == "amd-nr") return new LoadStatus(LoadState.Unsupported);
        if (!LoggingOn(ini)) return new LoadStatus(LoadState.LoggingOff);

        // Runs before logging was turned on (or before the install) don't count.
        var since = manifest.IniChanges.FirstOrDefault(c => c.Section == "Log" && c.Key == "LogToFile")?.ChangedUtc ?? manifest.InstalledUtc;
        var log = Path.Combine(targetDir, FileName);
        if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < since)
            return lastLaunchUtc is { } launched && launched > since && DateTime.UtcNow - launched > TimeSpan.FromSeconds(20)
                ? new LoadStatus(LoadState.DidNotLoad, RunLocal: launched.ToLocalTime())
                : new LoadStatus(LoadState.NotRunYet);

        List<string> lines;
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new LoadStatus(LoadState.NotRunYet); }
        var when = File.GetLastWriteTime(log);
        return Parse(lines, fsr4) with { RunLocal = when };
    }

    /// <summary>Works out what the game sent and what OptiScaler used from the log's lines.</summary>
    public static LoadStatus Parse(IReadOnlyList<string> lines, bool fsr4)
    {
        string? backend = null, sent = null;
        var fellBack = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // "NVSDK_NGX_D3D12_CreateFeature Creating new fsr31 upscaler" (also "Creating new {} feature" on DX11).
            var created = Between(line, "Creating new ", " upscaler") ?? Between(line, "Creating new ", " feature");
            if (created is not null && !created.Contains("DLSSG", StringComparison.OrdinalIgnoreCase) && !created.Contains("DLSSD", StringComparison.OrdinalIgnoreCase))
            {
                backend = created;
                sent = InputBefore(lines, i);
            }
            // The upscaler changed later in the run (OptiScaler's menu): the last one is the one in use.
            if (Between(line, "init successful for ", ", upscaler changed") is { } changed) backend = changed;
            if (line.Contains("returning to FSR 2.1.2", StringComparison.Ordinal) || line.Contains("falling back to FSR 2.1.2", StringComparison.Ordinal))
            {
                backend = "fsr21";
                fellBack = true;
            }
        }
        return backend is null
            ? new LoadStatus(LoadState.LoadedNothingTaken)
            : new LoadStatus(LoadState.Working, sent ?? "DLSS", BackendName(backend, fsr4), FellBack: fellBack);
    }

    /// <summary>
    /// What the game called, from the lines just before the upscaler was created: OptiScaler routes every input through
    /// its NGX path, so the input's own entry point (XeSS, FSR 2, FSR 3, FidelityFX API) shows up first; otherwise DLSS.
    /// </summary>
    private static string InputBefore(IReadOnlyList<string> lines, int index)
    {
        for (var i = index - 1; i >= Math.Max(0, index - 40); i--)
        {
            var l = lines[i];
            if (l.Contains("xessD3D12CreateContext", StringComparison.OrdinalIgnoreCase) || l.Contains("xessD3D11CreateContext", StringComparison.OrdinalIgnoreCase)
                || l.Contains("xessVKCreateContext", StringComparison.OrdinalIgnoreCase)) return "XeSS";
            if (l.Contains("ffxCreateContext", StringComparison.Ordinal)) return "FSR 3.1";
            if (l.Contains("Fsr3", StringComparison.Ordinal) && l.Contains("context created", StringComparison.Ordinal)) return "FSR 3";
            if (l.Contains("Fsr2", StringComparison.OrdinalIgnoreCase) && l.Contains("context created", StringComparison.Ordinal)) return "FSR 2";
            if (l.Contains("Creating new", StringComparison.Ordinal)) break;
        }
        return "DLSS";
    }

    /// <summary>OptiScaler's backend names in plain words.</summary>
    public static string BackendName(string backend, bool fsr4) => backend.ToLowerInvariant() switch
    {
        "xess" => "XeSS",
        "fsr21" => "FSR 2.1",
        "fsr22" => "FSR 2.2",
        "fsr31" => fsr4 ? "FSR 4" : "FSR 3.1",
        "dlss" => "DLSS",
        "dlssd" => "DLSS Ray Reconstruction",
        _ => backend
    };

    private static string? Between(string line, string start, string end)
    {
        var i = line.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = line.IndexOf(end, i, StringComparison.Ordinal);
        return j > i ? line[i..j].Trim() : null;
    }
}
