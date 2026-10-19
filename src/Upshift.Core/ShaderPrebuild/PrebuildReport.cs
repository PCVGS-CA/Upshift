using System.Globalization;
using System.Text;

namespace Upshift.Core.ShaderPrebuild;

/// <summary>
/// The before-and-after report: per game session, how many pipelines the game built, how many were slow (over 10 and
/// 20 ms), the longest and the total build time, and what came just before the session (a cleared cache, a pre-build or
/// a restored cache), so a cold run and a pre-built run can be compared. Read from the recorder's timing logs.
/// </summary>
public static class PrebuildReport
{
    public sealed record SessionStats(string Name, DateTime? Started, int Built, int Distinct, int Over10, int Over20, double LongestMs,
        double TotalMs, int Failed, double Minutes, int RayTracing, double RayTracingMs, int OnSoftware, bool RayTracingScene, string? Before);

    public static SessionStats Stats(ShaderLibrary.SessionInfo session, IReadOnlyList<(DateTime When, string What)> events)
    {
        var software = session.Cards.Where(c => c.Value?.Software == true).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int built = 0, over10 = 0, over20 = 0, failed = 0, rt = 0, onSoftware = 0;
        double longest = 0, total = 0, rtMs = 0, first = -1, last = 0;
        var ids = new HashSet<string>();
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(session.Folder, "timing.csv")).Skip(1))
            {
                // t_ms,kind,ms,result,record_id,new,thread,adapter,rec_ms
                var f = line.Split(',');
                if (f.Length < 5) continue;
                if (f.Length >= 8 && software.Contains(f[7])) { onSoftware++; continue; }
                double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms);
                if (f[1].StartsWith("stateobject")) { rt++; rtMs += ms; continue; }
                var ok = uint.TryParse(f[3], NumberStyles.HexNumber, null, out var hr) && hr < 0x80000000;
                if (!ok) { failed++; continue; }
                double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
                built++;
                if (ms > 10) over10++;
                if (ms > 20) over20++;
                longest = Math.Max(longest, ms);
                total += ms;
                if (f[4].Length > 0) ids.Add(f[4]);
                if (first < 0) first = t;
                last = t;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string? before = null;
        if (session.Started is { } start && events.Where(e => e.When <= start).OrderBy(e => e.When).LastOrDefault() is { What: { } what } ev)
            before = $"{what} {ev.When:d MMM HH:mm}";
        return new SessionStats(session.Name, session.Started, built, ids.Count, over10, over20, longest, total, failed,
            first < 0 ? 0 : (last - first) / 60000.0, rt, rtMs, onSoftware, session.RayTracingScene, before);
    }

    /// <summary>Pre-builds done for the game: when, and the summary lines (from replays\*.txt).</summary>
    public static List<(DateTime When, string What)> PrebuildEvents(string gameDir)
    {
        var events = new List<(DateTime, string)>();
        try
        {
            var dir = Path.Combine(gameDir, "replays");
            if (!Directory.Exists(dir)) return events;
            foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
                if (ShaderLibrary.ParseStamp(Path.GetFileNameWithoutExtension(file)) is { } t) events.Add((t, "pre-build"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return events;
    }

    /// <summary>
    /// Finished pre-builds for the game: when each started and ended. The summary (replays\*.txt) is named by the end
    /// time and holds the seconds it took. A pre-build that was stopped writes no summary, so it isn't listed.
    /// </summary>
    public static List<(DateTime Start, DateTime End)> PrebuildRuns(string gameDir)
    {
        var runs = new List<(DateTime, DateTime)>();
        try
        {
            var dir = Path.Combine(gameDir, "replays");
            if (!Directory.Exists(dir)) return runs;
            foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
            {
                if (ShaderLibrary.ParseStamp(Path.GetFileNameWithoutExtension(file)) is not { } end) continue;
                double seconds = 0;
                foreach (var line in File.ReadLines(file))
                    if (line.StartsWith("seconds\t", StringComparison.Ordinal))
                        double.TryParse(line[8..], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                runs.Add((end.AddSeconds(-Math.Ceiling(seconds)), end));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return runs.OrderBy(r => r.Item1).ToList();
    }

    /// <summary>The report as text (for "Copy as text"), with the graphics card and driver so PCs can be compared.</summary>
    public static string Text(string gameName, string program, string graphicsCard, string? driver, IReadOnlyList<SessionStats> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pre-build shaders report: {gameName} ({program})");
        sb.AppendLine($"Graphics card: {graphicsCard}{(driver is { Length: > 0 } ? $", driver {driver}" : "")}");
        sb.AppendLine($"Made {DateTime.Now:d MMM yyyy, HH:mm} by Upshift");
        sb.AppendLine();
        if (sessions.Count == 0) { sb.AppendLine("No game sessions have been recorded yet."); return sb.ToString(); }
        sb.AppendLine($"{"Session",-16}{"Built",8}{"Distinct",10}{"> 10 ms",9}{"> 20 ms",9}{"Longest ms",12}{"Total ms",11}{"Failed",8}{"Minutes",9}  Before");
        sb.AppendLine(new string('-', 112));
        foreach (var s in sessions)
            sb.AppendLine($"{s.Started?.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) ?? s.Name,-16}{s.Built,8}{s.Distinct,10}{s.Over10,9}{s.Over20,9}" +
                          $"{s.LongestMs,12:0.0}{s.TotalMs,11:0}{s.Failed,8}{s.Minutes,9:0.0}  {s.Before ?? "–"}");
        sb.AppendLine();
        sb.AppendLine("Built: pipelines the game created (each creation counted). Distinct: different pipelines among them.");
        sb.AppendLine("> 10 ms / > 20 ms: how many took the driver longer than that, the ones that can stutter. Total: all build time added up.");
        sb.AppendLine("Failed: requests the driver refused (the game asked and carried on); they're never pre-built.");
        sb.AppendLine("Before: what happened last before the session started (a cleared cache, a pre-build, a restored cache).");
        foreach (var s in sessions)
        {
            if (s.RayTracing > 0) sb.AppendLine($"{s.Name}: ray tracing: {s.RayTracing} ray tracing pipelines built ({s.RayTracingMs:0} ms); they aren't learned or pre-built.");
            else if (s.RayTracingScene) sb.AppendLine($"{s.Name}: ray tracing: the game built ray tracing scenes.");
            if (s.OnSoftware > 0) sb.AppendLine($"{s.Name}: {s.OnSoftware} creations on a software renderer aren't counted.");
        }
        return sb.ToString();
    }
}
