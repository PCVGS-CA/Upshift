using System.Globalization;
using System.Text;
using Upshift.Core.Measure;
using Upshift.Core.Services;

namespace Upshift.Core.ShaderPrebuild;

public enum SessionKind { Normal, ColdStart, PreBuilt }

/// <summary>One recorded play session with its label, when it ended, and a Measure run made during it (if any).</summary>
public sealed record LabelledSession(PrebuildReport.SessionStats Stats, SessionKind Kind, DateTime Started, DateTime? Ended, MeasurementRun? Measure)
{
    public double? PlayMinutes => Ended is { } end ? Math.Max(0, (end - Started).TotalMinutes) : null;
}

/// <summary>The latest Cold start and Pre-built sessions side by side, the verdict, and what's missing to make one.</summary>
public sealed record StutterComparison(LabelledSession? Cold, LabelledSession? PreBuilt, string? Verdict, double? CutPercent, IReadOnlyList<string> Missing)
{
    public bool HasAny => Cold is not null || PreBuilt is not null;
}

/// <summary>
/// "Did it help?" for Pre-build shaders: each recorded play session is labelled by what came just before it. The first
/// session of the game after "Clear driver shader cache" is a Cold start, the first after "Pre-build now" is Pre-built,
/// and every other session is Normal (a second session after a clear finds the shaders cached again). The latest of
/// each are compared; the verdict uses builds over 10 ms (the ones that stutter), or the total build time when the cold
/// start had none.
/// </summary>
public static class StutterReport
{
    public const string ColdEvent = "cache cleared", PrebuildEvent = "pre-build";

    public static string KindName(SessionKind kind) => kind switch
    {
        SessionKind.ColdStart => "Cold start",
        SessionKind.PreBuilt => "Pre-built",
        _ => "Normal"
    };

    public static List<LabelledSession> Label(IReadOnlyList<ShaderLibrary.SessionInfo> sessions, IReadOnlyList<(DateTime When, string What)> events,
        IReadOnlyList<MeasurementRun> runs)
    {
        var ordered = sessions.Where(s => s.Started is not null).OrderBy(s => s.Started).ToList();
        var sorted = events.OrderBy(e => e.When).ToList();
        var result = new List<LabelledSession>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var session = ordered[i];
            var start = session.Started!.Value;
            var kind = SessionKind.Normal;
            if (sorted.Where(e => e.When <= start).LastOrDefault() is { What: { } what } last
                && (what == ColdEvent || what == PrebuildEvent)
                && !ordered.Take(i).Any(earlier => earlier.Started > last.When))   // the first session after it
                kind = what == ColdEvent ? SessionKind.ColdStart : SessionKind.PreBuilt;
            var end = SessionEnd(session.Folder);
            result.Add(new LabelledSession(PrebuildReport.Stats(session, sorted), kind, start, end, MeasureDuring(runs, start, end)));
        }
        return result;
    }

    /// <summary>
    /// When the session ended: the last time the recorder wrote anything (its 30-second "alive" file, the timing log or the
    /// notes). Sessions from before the alive file end at their last shader build, so their play time can be short.
    /// </summary>
    public static DateTime? SessionEnd(string folder)
    {
        DateTime? end = null;
        foreach (var name in new[] { "alive.txt", "timing.csv", "session.txt" })
        {
            try
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path) && File.GetLastWriteTime(path) is var t && (end is null || t > end)) end = t;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return end;
    }

    /// <summary>The latest Measure run that overlaps the session (a run's time is its end; it started Seconds earlier).</summary>
    private static MeasurementRun? MeasureDuring(IReadOnlyList<MeasurementRun> runs, DateTime start, DateTime? end)
    {
        if (end is null) return null;
        var from = start.ToUniversalTime();
        var to = end.Value.ToUniversalTime().AddSeconds(30);   // the alive file is written every 30 seconds
        return runs.Where(r => r.Utc.AddSeconds(-r.Seconds) <= to && r.Utc >= from).OrderByDescending(r => r.Utc).FirstOrDefault();
    }

    public static StutterComparison Compare(IReadOnlyList<LabelledSession> labelled)
    {
        var cold = labelled.LastOrDefault(s => s.Kind == SessionKind.ColdStart);
        var pre = labelled.LastOrDefault(s => s.Kind == SessionKind.PreBuilt);
        var missing = new List<string>();
        if (cold is null)
            missing.Add("No Cold start session yet: under Test tools, press Clear driver shader cache, then play the game for a few minutes.");
        if (pre is null)
            missing.Add("No Pre-built session yet: under Test tools, press Clear driver shader cache, then Pre-build now, then play the same part of the game.");
        if (cold is null || pre is null) return new(cold, pre, null, null, missing);

        double c = cold.Stats.Over10, p = pre.Stats.Over10;
        var what = "Shader stutter";
        if (c == 0) { c = cold.Stats.TotalMs; p = pre.Stats.TotalMs; what = "Shader build time"; }
        string verdict;
        double? cut = null;
        if (c <= 0) verdict = "The cold start built no shaders worth noticing, so there was nothing to cut.";
        else if (p >= c) verdict = $"No improvement: the pre-built session built {Count(pre, what)} against {Count(cold, what)} in the cold start.";
        else
        {
            cut = (c - p) / c * 100;
            verdict = $"{what} cut by {Math.Floor(cut.Value):0}%";
        }
        if (cold.PlayMinutes is { } cm && pre.PlayMinutes is { } pm && cm > 0 && pm > 0 && Math.Max(cm, pm) / Math.Min(cm, pm) > 2)
            verdict += " (the two sessions' play times differ a lot; compare the same part of the game for a fair result)";
        return new(cold, pre, verdict, cut, missing);
    }

    private static string Count(LabelledSession s, string what) =>
        what == "Shader stutter" ? $"{s.Stats.Over10:N0} builds over 10 ms" : $"{Ms(s.Stats.TotalMs)} of building";

    // ---------------- Sharing ----------------

    /// <summary>The comparison's rows: (label, Cold start, Pre-built). The card, the text and the picture use these.</summary>
    public static List<(string Label, string Cold, string PreBuilt)> Rows(StutterComparison c)
    {
        string V(LabelledSession? s, Func<LabelledSession, string> f) => s is null ? "–" : f(s);
        var rows = new List<(string, string, string)>
        {
            ("Played", V(c.Cold, s => s.Started.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)), V(c.PreBuilt, s => s.Started.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture))),
            ("Shaders built while playing", V(c.Cold, s => s.Stats.Built.ToString("N0", CultureInfo.InvariantCulture)), V(c.PreBuilt, s => s.Stats.Built.ToString("N0", CultureInfo.InvariantCulture))),
            ("Time spent building", V(c.Cold, s => Ms(s.Stats.TotalMs)), V(c.PreBuilt, s => Ms(s.Stats.TotalMs))),
            ("Builds over 10 ms", V(c.Cold, s => s.Stats.Over10.ToString("N0", CultureInfo.InvariantCulture)), V(c.PreBuilt, s => s.Stats.Over10.ToString("N0", CultureInfo.InvariantCulture))),
            ("Longest single build", V(c.Cold, s => Ms(s.Stats.LongestMs)), V(c.PreBuilt, s => Ms(s.Stats.LongestMs))),
            ("Play time", V(c.Cold, s => Minutes(s.PlayMinutes)), V(c.PreBuilt, s => Minutes(s.PlayMinutes)))
        };
        if (c.Cold?.Measure is not null || c.PreBuilt?.Measure is not null)
        {
            rows.Add(("1% low (Measure)", V(c.Cold, s => s.Measure is { } m ? $"{Math.Round(m.LowFps)} fps" : "not measured"),
                V(c.PreBuilt, s => s.Measure is { } m ? $"{Math.Round(m.LowFps)} fps" : "not measured")));
            rows.Add(("Frame-time spikes (Measure)", V(c.Cold, s => SpikesText(s.Measure)), V(c.PreBuilt, s => SpikesText(s.Measure))));
        }
        return rows;
    }

    /// <summary>"Copy as text": the game, the PC, the rows side by side and the verdict.</summary>
    public static string Text(string gameName, StutterComparison c, PcInfo pc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pre-build shaders: did it help? {gameName}");
        sb.AppendLine($"Graphics card: {pc.GraphicsText}");
        sb.AppendLine($"Processor:     {pc.ProcessorText}");
        sb.AppendLine($"Windows:       {pc.WindowsText}");
        sb.AppendLine();
        var rows = Rows(c);
        var width = rows.Max(r => r.Label.Length) + 2;
        sb.AppendLine($"{"".PadRight(width)}{"Cold start",-22}Pre-built");
        foreach (var (label, cold, pre) in rows) sb.AppendLine($"{label.PadRight(width)}{cold,-22}{pre}");
        sb.AppendLine();
        if (c.Verdict is { } v) sb.AppendLine(v);
        foreach (var m in c.Missing) sb.AppendLine(m);
        sb.AppendLine("Measured with Upshift (Pre-build shaders, Experimental). Frame-time spikes: frames over twice the usual frame time.");
        return sb.ToString();
    }

    private static readonly string[] CsvColumns =
    {
        "game", "verdict", "stutter_cut_pct",
        "cold_played", "cold_built", "cold_build_ms", "cold_over_10ms", "cold_longest_ms", "cold_play_minutes", "cold_low_fps", "cold_spikes",
        "prebuilt_played", "prebuilt_built", "prebuilt_build_ms", "prebuilt_over_10ms", "prebuilt_longest_ms", "prebuilt_play_minutes", "prebuilt_low_fps", "prebuilt_spikes",
        "processor", "graphics_card", "driver", "windows"
    };

    /// <summary>
    /// One row per game, for "Export all results". Numbers use a dot, dates are UTC; each row carries the PC (never the
    /// user name, the PC name or a folder path).
    /// </summary>
    public static string Csv(IEnumerable<(string GameName, StutterComparison Comparison)> games, PcInfo pc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvColumns));
        foreach (var (name, c) in games)
        {
            IEnumerable<string> Side(LabelledSession? s) => s is null
                ? Enumerable.Repeat("", 8)
                : new[]
                {
                    s.Started.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), s.Stats.Built.ToString(CultureInfo.InvariantCulture),
                    N(s.Stats.TotalMs, 0), s.Stats.Over10.ToString(CultureInfo.InvariantCulture), N(s.Stats.LongestMs, 1), N(s.PlayMinutes, 1),
                    N(s.Measure?.LowFps, 1), s.Measure?.Spikes?.ToString(CultureInfo.InvariantCulture) ?? ""
                };
            var cells = new[] { name, c.Verdict ?? "", N(c.CutPercent, 0) }
                .Concat(Side(c.Cold)).Concat(Side(c.PreBuilt))
                .Concat(new[] { pc.Processor ?? "", pc.GraphicsCard ?? "", pc.DriverVersion ?? "", pc.Windows ?? "" });
            sb.AppendLine(string.Join(",", cells.Select(Cell)));
        }
        return sb.ToString();
    }

    public static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";

    private static string Minutes(double? minutes) => minutes is { } m ? (m < 1 ? "under a minute" : $"{m:0} min") : "–";

    private static string SpikesText(MeasurementRun? run) =>
        run is null ? "not measured"
        : run.Spikes is not { } n ? "not recorded"
        : run.Seconds > 0 ? $"{n:N0} ({n / (run.Seconds / 60):0.0} a minute)" : $"{n:N0}";

    private static string N(double? v, int digits) => v is { } x ? Math.Round(x, digits).ToString(CultureInfo.InvariantCulture) : "";

    private static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+-@".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
