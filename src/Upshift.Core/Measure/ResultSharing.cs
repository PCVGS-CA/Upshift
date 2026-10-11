using System.Globalization;
using System.Text;
using Upshift.Core.Services;

namespace Upshift.Core.Measure;

/// <summary>
/// Measure results for sharing: "Copy as text", the picture, and "Export all results" (CSV). They carry the game, the
/// user's note, the upscaler and frame generation, the frame rates, the verdict, the length and date, and the PC's
/// processor, graphics card, memory and Windows version. Never the user name, the PC name or a folder path.
/// </summary>
public static class ResultSharing
{
    /// <summary>The lines of one result, in order, as (label, value). The picture and the text both use these.</summary>
    public static List<(string Label, string Value)> Lines(string gameName, MeasurementRun run, PcInfo pc)
    {
        var lines = new List<(string, string)>
        {
            ("Game", gameName),
        };
        if (run.Note is { Length: > 0 } note) lines.Add(("Settings", note));
        lines.Add(("Upscaler", run.Settings.Upscaler is { Length: > 0 } u ? u : "–"));
        lines.Add(("Frame generation", run.FrameGenOn ? (run.Settings.FrameGeneration is { Length: > 0 } fg && fg != "Off" ? fg : "On") : "Off"));
        lines.Add(("Average", Fps(run.AverageFps)));
        lines.Add(("Low (1%)", Fps(run.LowFps)));
        lines.Add(("Max (best second)", run.MaxFps is { } max ? Fps(max) : "–"));
        lines.Add(("Limited by", run.Bottleneck is { } b ? b.Title.Replace("Limited by your ", "your ") + (b.LessCertain ? " (less certain)" : "") : "–"));
        lines.Add(("Length", LengthText(run.Seconds) + (run.StoppedEarly ? " (stopped early)" : "")));
        lines.Add(("Measured", run.Utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)));
        lines.Add(("Processor", pc.ProcessorText));
        lines.Add(("Graphics card", pc.GraphicsText));
        lines.Add(("Memory", pc.MemoryText));
        lines.Add(("Windows", pc.WindowsText));
        return lines;
    }

    /// <summary>"Copy as text": one line per item, then "Measured with Upshift".</summary>
    public static string Text(string gameName, MeasurementRun run, PcInfo pc)
    {
        var lines = Lines(gameName, run, pc);
        var width = lines.Max(l => l.Label.Length) + 2;
        var sb = new StringBuilder();
        foreach (var (label, value) in lines) sb.Append((label + ":").PadRight(width)).AppendLine(value);
        sb.AppendLine("Measured with Upshift (PresentMon frame timings).");
        return sb.ToString();
    }

    private static readonly string[] CsvColumns =
    {
        "game", "measured", "note", "upscaler", "frame_generation", "average_fps", "low_fps", "max_fps", "limited_by", "less_certain",
        "seconds", "stopped_early", "gpu_busy_ms", "gpu_wait_ms", "cpu_busy_ms", "cpu_wait_ms", "gpu_usage_pct", "cpu_usage_pct",
        "busiest_core_pct", "vram_peak_gb", "vram_total_gb", "frame_cap", "fsr4", "optiscaler", "processor", "graphics_card",
        "driver", "memory_gb", "windows"
    };

    /// <summary>
    /// "Export all results": one row per run, every game. Numbers use a dot and no thousands separator, so files from
    /// different PCs can be combined; dates are UTC (ISO 8601). Each row carries the PC, so rows can be mixed freely.
    /// </summary>
    public static string Csv(IEnumerable<(string GameName, MeasurementRun Run)> runs, PcInfo pc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvColumns));
        foreach (var (name, r) in runs)
        {
            var cells = new[]
            {
                name, r.Utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), r.Note ?? "",
                r.Settings.Upscaler, r.FrameGenOn ? (r.Settings.FrameGeneration is { Length: > 0 } fg && fg != "Off" ? fg : "On") : "Off",
                N(r.AverageFps), N(r.LowFps), N(r.MaxFps), r.Verdict?.ToString() ?? "", r.Verdict is null ? "" : r.VerdictLessCertain ? "yes" : "no",
                N(r.Seconds), r.StoppedEarly ? "yes" : "no", N(r.GpuBusyMs, 2), N(r.GpuWaitMs, 2), N(r.CpuBusyMs, 2), N(r.CpuWaitMs, 2),
                N(r.GpuPercent), N(r.CpuPercent), N(r.BusiestCorePercent),
                N(r.PeakVramBytes is { } p ? p / 1073741824.0 : null, 2), N(r.TotalVramBytes is { } t ? t / 1073741824.0 : null, 2),
                r.Settings.FrameCap, r.Settings.Fsr4, r.Settings.OptiScaler,
                pc.Processor ?? "", pc.GraphicsCard ?? "", pc.DriverVersion ?? "", N(pc.MemoryGb, 0), pc.Windows ?? ""
            };
            sb.AppendLine(string.Join(",", cells.Select(Cell)));
        }
        return sb.ToString();
    }

    private static string Fps(double fps) => $"{Math.Round(fps)} fps";

    private static string LengthText(double seconds)
    {
        var s = (int)Math.Round(seconds);
        return s < 120 ? $"{s} seconds" : s % 60 == 0 ? $"{s / 60} minutes" : $"{s / 60} min {s % 60} s";
    }

    private static string N(double? v, int digits = 1) => v is { } x ? Math.Round(x, digits).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>A CSV cell: quoted when it holds a comma, quote or line break; a leading = + - @ is defused for spreadsheets.</summary>
    private static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+-@".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
