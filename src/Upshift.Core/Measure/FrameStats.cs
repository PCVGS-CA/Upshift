using System.Globalization;

namespace Upshift.Core.Measure;

/// <summary>What one PresentMon capture says about a game's frame rate.</summary>
/// <param name="Frames">Frames presented by the game (generated frames included).</param>
/// <param name="Seconds">Time those frames covered.</param>
/// <param name="AverageFps">Frames per second over the whole capture.</param>
/// <param name="LowFps">The "1% low": the average frame rate of the slowest 1% of frames.</param>
/// <param name="GeneratedFrames">Frames PresentMon marked as generated (frame generation), when it can tell.</param>
/// <param name="MaxFps">The highest one-second average: the frames in the best whole second of the run. Null when the run
/// was shorter than one whole second. A single very fast frame (a loading screen) can't set it on its own.</param>
/// <param name="Timing">Where each frame's time went (CPU and GPU busy and waiting), when PresentMon recorded it.</param>
/// <param name="Spikes">Frame-time spikes: frames that took more than twice the usual (median) frame time, the hitches you
/// feel as stutter (a shader being built, say).</param>
public sealed record FrameStats(int Frames, double Seconds, double AverageFps, double LowFps, int GeneratedFrames, bool FrameTypeKnown, double? MaxFps = null,
    FrameTiming? Timing = null, int? Spikes = null);

/// <summary>
/// Averages per rendered frame from PresentMon 2.6.0's default CSV (column names checked against its source,
/// CsvOutput.cpp, and a real recording): MsCPUBusy / MsCPUWait (the game's CPU work on a frame, then waiting, e.g. in
/// Present), MsGPUBusy / MsGPUWait (the GPU working on the frame, then idle until the next one). The "--v2_metrics"
/// names (CPUBusy…) are accepted too. Each average is null when its column is missing or held no numbers. Generated
/// frames are left out when PresentMon marks them: the game's CPU and GPU work belongs to the frames it rendered.
/// </summary>
/// <param name="FrameMs">The average time between the game's own frames: CPU busy + CPU wait when both exist.</param>
/// <param name="VSyncShare">The share of frames presented with SyncInterval 1 or more (VSync on); null when unknown.</param>
public sealed record FrameTiming(double? CpuBusyMs, double? CpuWaitMs, double? GpuBusyMs, double? GpuWaitMs, double FrameMs, double? VSyncShare);

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
        var cpuBusyCol = Col("MsCPUBusy", "CPUBusy");
        var cpuWaitCol = Col("MsCPUWait", "CPUWait");
        var gpuBusyCol = Col("MsGPUBusy", "GPUBusy");
        var gpuWaitCol = Col("MsGPUWait", "GPUWait");
        var syncCol = Col("SyncInterval");
        if (timeCol < 0) return null;

        var bySwapChain = new Dictionary<string, List<Frame>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var cells = line.Split(',');
            if (cells.Length <= timeCol) continue;
            if (pidCol >= 0 && (!int.TryParse(cells[pidCol], out var pid) || pid != processId)) continue;
            if (Number(cells, timeCol) is not { } ms || ms <= 0 || ms > 5000) continue;
            var key = swapCol >= 0 && swapCol < cells.Length ? cells[swapCol] : "";
            if (!bySwapChain.TryGetValue(key, out var list)) bySwapChain[key] = list = new();
            list.Add(new Frame(ms, typeCol >= 0 && typeCol < cells.Length ? cells[typeCol].Trim() : null,
                Number(cells, cpuBusyCol), Number(cells, cpuWaitCol), Number(cells, gpuBusyCol), Number(cells, gpuWaitCol), Number(cells, syncCol)));
        }

        var frames = bySwapChain.Values.OrderByDescending(l => l.Count).FirstOrDefault();
        if (frames is null || frames.Count < 10) return null;
        var times = frames.Select(f => f.Ms).ToList();
        var seconds = times.Sum() / 1000.0;
        // The slowest 1% of frames (at least one), averaged: the frame rate the game drops to in its worst moments.
        var slowest = times.OrderByDescending(t => t).Take(Math.Max(1, times.Count / 100)).Average();
        var generated = frames.Count(f => IsGenerated(f.Type));
        return new FrameStats(frames.Count, seconds, frames.Count / seconds, 1000.0 / slowest, generated, typeCol >= 0, MaxOneSecondFps(times),
            Timing(frames), Spikes(times));
    }

    /// <summary>Frames that took more than twice the median frame time.</summary>
    public static int Spikes(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0) return 0;
        var sorted = frameTimesMs.OrderBy(t => t).ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return frameTimesMs.Count(t => t > 2 * median);
    }

    private sealed record Frame(double Ms, string? Type, double? CpuBusy, double? CpuWait, double? GpuBusy, double? GpuWait, double? Sync);

    /// <summary>The averages over the frames the game rendered itself; null when PresentMon recorded no busy/wait columns.</summary>
    private static FrameTiming? Timing(List<Frame> all)
    {
        var rendered = all.Where(f => !IsGenerated(f.Type)).ToList();
        if (rendered.Count == 0) rendered = all;
        double? Avg(Func<Frame, double?> pick)
        {
            var values = rendered.Select(pick).Where(v => v is >= 0 and < 5000).Select(v => v!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }
        var cpuBusy = Avg(f => f.CpuBusy);
        var cpuWait = Avg(f => f.CpuWait);
        var gpuBusy = Avg(f => f.GpuBusy);
        var gpuWait = Avg(f => f.GpuWait);
        if (cpuBusy is null && gpuBusy is null) return null;
        var frameMs = Avg(f => f.CpuBusy is { } b && f.CpuWait is { } w ? b + w : null) ?? rendered.Average(f => f.Ms);
        var syncs = all.Where(f => f.Sync is not null).ToList();
        double? vsync = syncs.Count == 0 ? null : syncs.Count(f => f.Sync >= 1) / (double)syncs.Count;
        return new FrameTiming(cpuBusy, cpuWait, gpuBusy, gpuWait, frameMs, vsync);
    }

    /// <summary>A number from a cell; null for a missing column, "NA" or anything else that isn't a number.</summary>
    private static double? Number(string[] cells, int column) =>
        column >= 0 && column < cells.Length && double.TryParse(cells[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// The highest one-second average: the run is cut into whole seconds by when each frame ended, and the second with
    /// the most frames gives the result. The last, unfinished second isn't counted. Null for runs under one second.
    /// </summary>
    public static double? MaxOneSecondFps(IReadOnlyList<double> frameTimesMs)
    {
        var perSecond = new Dictionary<int, int>();
        double clock = 0;
        foreach (var ms in frameTimesMs)
        {
            clock += ms;
            var second = (int)(clock / 1000.0);
            perSecond[second] = perSecond.TryGetValue(second, out var n) ? n + 1 : 1;
        }
        var whole = (int)(clock / 1000.0);   // seconds 0 .. whole-1 are complete
        var counts = perSecond.Where(p => p.Key < whole).Select(p => p.Value).ToList();
        return counts.Count == 0 ? null : counts.Max();
    }

    /// <summary>
    /// PresentMon's FrameType: "Application" for frames the game rendered; vendor names (Intel_XEFG, AMD_AFMF, …) for
    /// frames a driver or SDK generated. "Repeated", "NotSet", "Unspecified" and "NA" aren't generated frames.
    /// </summary>
    public static bool IsGenerated(string? frameType) =>
        !string.IsNullOrWhiteSpace(frameType)
        && !new[] { "Application", "Repeated", "NotSet", "Unspecified", "NA", "Unknown" }.Contains(frameType, StringComparer.OrdinalIgnoreCase);
}
