using System.Globalization;

namespace Upshift.Core.Measure;

/// <summary>What one PresentMon capture says about a game's frame rate.</summary>
/// <param name="Frames">Frames presented by the game (generated frames included).</param>
/// <param name="Seconds">Time those frames covered.</param>
/// <param name="AverageFps">Frames per second over the whole capture.</param>
/// <param name="LowFps">The "1% low": the average frame rate of the slowest 1% of frames.</param>
/// <param name="GeneratedFrames">Frames PresentMon marked as generated (frame generation), when it can tell.</param>
public sealed record FrameStats(int Frames, double Seconds, double AverageFps, double LowFps, int GeneratedFrames, bool FrameTypeKnown);

/// <summary>Reads PresentMon's CSV (console app 2.x).</summary>
public static class PresentMonCsv
{
    /// <summary>
    /// The game's frames from the capture: the swap chain with the most frames for that process id. Frame times come
    /// from MsBetweenPresents (or FrameTime). Null when nothing usable was recorded.
    /// </summary>
    public static FrameStats? Read(string csvPath, int processId)
    {
        if (!File.Exists(csvPath)) return null;
        using var reader = new StreamReader(csvPath);
        var header = reader.ReadLine();
        if (header is null) return null;
        var columns = header.Split(',').Select(c => c.Trim()).ToList();
        int Col(params string[] names) => names.Select(n => columns.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
        var pidCol = Col("ProcessID");
        var swapCol = Col("SwapChainAddress");
        var timeCol = Col("MsBetweenPresents", "FrameTime");
        var typeCol = Col("FrameType");
        if (timeCol < 0) return null;

        var bySwapChain = new Dictionary<string, List<(double Ms, string? Type)>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var cells = line.Split(',');
            if (cells.Length <= timeCol) continue;
            if (pidCol >= 0 && (!int.TryParse(cells[pidCol], out var pid) || pid != processId)) continue;
            if (!double.TryParse(cells[timeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 5000) continue;
            var key = swapCol >= 0 && swapCol < cells.Length ? cells[swapCol] : "";
            if (!bySwapChain.TryGetValue(key, out var list)) bySwapChain[key] = list = new();
            list.Add((ms, typeCol >= 0 && typeCol < cells.Length ? cells[typeCol].Trim() : null));
        }

        var frames = bySwapChain.Values.OrderByDescending(l => l.Count).FirstOrDefault();
        if (frames is null || frames.Count < 10) return null;
        var times = frames.Select(f => f.Ms).ToList();
        var seconds = times.Sum() / 1000.0;
        // The slowest 1% of frames (at least one), averaged: the frame rate the game drops to in its worst moments.
        var slowest = times.OrderByDescending(t => t).Take(Math.Max(1, times.Count / 100)).Average();
        var generated = frames.Count(f => IsGenerated(f.Type));
        return new FrameStats(frames.Count, seconds, frames.Count / seconds, 1000.0 / slowest, generated, typeCol >= 0);
    }

    /// <summary>
    /// PresentMon's FrameType: "Application" for frames the game rendered; vendor names (Intel_XEFG, AMD_AFMF, …) for
    /// frames a driver or SDK generated. "Repeated", "NotSet", "Unspecified" and "NA" aren't generated frames.
    /// </summary>
    public static bool IsGenerated(string? frameType) =>
        !string.IsNullOrWhiteSpace(frameType)
        && !new[] { "Application", "Repeated", "NotSet", "Unspecified", "NA", "Unknown" }.Contains(frameType, StringComparer.OrdinalIgnoreCase);
}
