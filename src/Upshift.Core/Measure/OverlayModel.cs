using System.Globalization;

namespace Upshift.Core.Measure;

/// <summary>The overlay's three states: armed and waiting for the key (yellow), recording (red), the result (teal).</summary>
public enum OverlayPhase { Ready, Recording, Result }

/// <summary>Windows' own counters right now, for the overlay's tiles (null where a PC doesn't have the counter).</summary>
public sealed record LiveLoad(double? GpuPercent, double? CpuPercent, long? VramUsedBytes);

/// <summary>One tile: a small label over a value ("1% low" / "98").</summary>
public sealed record OverlayTile(string Key, string Label, string Value);

/// <summary>Everything the overlay draws for one update; built by <see cref="OverlayItems.Build"/>.</summary>
public sealed record OverlayFrame
{
    public OverlayPhase Phase { get; init; }
    /// <summary>The measuring key's name, drawn as a key cap ("F10").</summary>
    public string KeyName { get; init; } = "F10";
    /// <summary>The planned length, "60 s" or "5:00".</summary>
    public string LengthText { get; init; } = "";
    /// <summary>Ready: the card's and the processor's use right now, for the two bars (null: "—").</summary>
    public double? GpuPercent { get; init; }
    public double? CpuPercent { get; init; }
    /// <summary>Result: the verdict's first advice sentence, under its title.</summary>
    public string? VerdictAdvice { get; init; }
    /// <summary>Result: seconds until it fades ("Hides in 8 s").</summary>
    public int HidesIn { get; init; }
    /// <summary>The big number (live FPS), or null when FPS isn't ticked or not known yet ("—" is drawn).</summary>
    public bool ShowFps { get; init; }
    public double? Fps { get; init; }
    public bool ShowGraph { get; init; }
    public IReadOnlyList<double> Graph { get; init; } = Array.Empty<double>();
    public IReadOnlyList<OverlayTile> Tiles { get; init; } = Array.Empty<OverlayTile>();
    /// <summary>0..1 while recording with the progress bar ticked, else null.</summary>
    public double? Progress { get; init; }
    /// <summary>"0:18 / 1:00" (recording), or null.</summary>
    public string? Timer { get; init; }
    /// <summary>The result's bottleneck line, or null.</summary>
    public string? Verdict { get; init; }
    /// <summary>"Press F10 to start · 60 s", "F10 stop & keep", "1:00 measured".</summary>
    public string Footer { get; init; } = "";
    /// <summary>The minimal style's one line: the first ticked value, and its timer text.</summary>
    public string MinimalValue { get; init; } = "";
    public string MinimalTimer { get; init; } = "";
}

/// <summary>
/// What the overlay can show ("Show on overlay" in Settings), and the text for each. Values come only from PresentMon
/// and Windows' counters; anything a game or PC doesn't report is "—", never a guess.
/// </summary>
public static class OverlayItems
{
    public const string Fps = "fps", Average = "average", Low1 = "low1", Low01 = "low01", Max = "max", FrameTime = "frametime",
        Latency = "latency", Gpu = "gpu", Cpu = "cpu", GpuBusy = "gpubusy", Vram = "vram", Graph = "graph", Progress = "progress";

    public const string Dash = "—";

    /// <summary>Ticked by default.</summary>
    public static readonly string[] Defaults = { Fps, Average, Low1, Graph, Progress };

    /// <summary>Every item in the order of the checkboxes and the tiles.</summary>
    public static readonly (string Key, string Label)[] All =
    {
        (Fps, "FPS"), (Average, "Average"), (Low1, "1% low"), (Graph, "Frame-time line"), (Progress, "Progress and timer"),
        (Low01, "0.1% low"), (Max, "Max FPS"), (FrameTime, "Frame time (ms)"), (Latency, "Latency"), (Gpu, "GPU %"),
        (Cpu, "CPU %"), (GpuBusy, "GPU busy / wait"), (Vram, "Video memory used")
    };

    private static readonly string[] TileKeys = { Average, Low1, Low01, Max, FrameTime, Latency, Gpu, Cpu, GpuBusy, Vram };

    /// <summary>The saved choice with unknown keys dropped; the defaults when nothing was saved.</summary>
    public static List<string> Normalize(IEnumerable<string>? saved) =>
        saved is null ? Defaults.ToList() : All.Select(a => a.Key).Where(k => saved.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>"1:00", "2:30", "30:00".</summary>
    public static string Clock(double seconds)
    {
        var s = Math.Max(0, (int)Math.Floor(seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"60 s" under two minutes, else "5:00".</summary>
    public static string Length(double seconds) => seconds < 120 ? $"{Math.Round(seconds):0} s" : Clock(seconds);

    private static string Whole(double? v) => v is { } x && !double.IsNaN(x) ? Math.Round(x).ToString("0", CultureInfo.InvariantCulture) : Dash;
    /// <summary>Frame rates in tiles: one decimal ("83.6"), as in the mockup.</summary>
    private static string Rate(double? v) => v is { } x && !double.IsNaN(x) ? x.ToString("0.0", CultureInfo.InvariantCulture) : Dash;

    /// <summary>A tile's label and value from the live figures.</summary>
    public static OverlayTile Tile(string key, LiveSnapshot? snap, LiveLoad? load) => key switch
    {
        Average => new(key, "Average", Rate(snap?.AverageFps)),
        Low1 => new(key, "1% low", Rate(snap?.LowFps)),
        Low01 => new(key, "0.1% low", Rate(snap?.Low01Fps)),
        Max => new(key, "Max", Rate(snap?.MaxFps)),
        FrameTime => new(key, "Frame", snap?.FrameMs is { } ms ? ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : Dash),
        Latency => new(key, "Latency", snap?.LatencyMs is { } l ? Whole(l) + " ms" : Dash),
        Gpu => new(key, "GPU", load?.GpuPercent is { } g ? Whole(g) + "%" : Dash),
        Cpu => new(key, "CPU", load?.CpuPercent is { } c ? Whole(c) + "%" : Dash),
        GpuBusy => new(key, "GPU busy/wait", snap?.GpuBusyMs is { } b
            ? b.ToString("0.0", CultureInfo.InvariantCulture) + " / " + (snap.GpuWaitMs is { } w ? w.ToString("0.0", CultureInfo.InvariantCulture) : Dash)
            : Dash),
        Vram => new(key, "Video memory", load?.VramUsedBytes is { } v && v > 0
            ? (v / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : Dash),
        _ => new(key, key, Dash)
    };

    /// <summary>
    /// One update of the overlay. Ready: live FPS and the ticked values. Recording: the same, plus the frame-time line and
    /// the progress bar when ticked, with figures counted from the start of the recording. Result: always Average, 1% low and
    /// Max, the verdict and how long it measured.
    /// </summary>
    /// <param name="live">The capture from its start (live FPS, frame time, graph).</param>
    /// <param name="recorded">The recording only (average, lows, max), or null outside a recording.</param>
    public static OverlayFrame Build(OverlayPhase phase, IReadOnlyList<string> items, string keyName, double plannedSeconds,
        LiveSnapshot? live, LiveSnapshot? recorded, LiveLoad? load, double elapsedSeconds = 0,
        FrameStats? result = null, string? verdict = null, string? verdictAdvice = null)
    {
        bool On(string k) => items.Contains(k, StringComparer.OrdinalIgnoreCase);
        if (phase == OverlayPhase.Result)
        {
            var tiles = new List<OverlayTile>
            {
                new(Average, "Average", Rate(result?.AverageFps)), new(Low1, "1% low", Rate(result?.LowFps)), new(Max, "Max", Rate(result?.MaxFps))
            };
            return new OverlayFrame
            {
                Phase = phase, KeyName = keyName, Tiles = tiles, Verdict = verdict, VerdictAdvice = verdictAdvice, HidesIn = 8,
                Footer = Clock(result?.Seconds ?? elapsedSeconds) + " measured",
                MinimalValue = $"Avg {Whole(result?.AverageFps)} · 1% {Whole(result?.LowFps)}", MinimalTimer = Clock(result?.Seconds ?? elapsedSeconds)
            };
        }

        // Average, the lows and the max describe the recording once it runs; before it, the capture so far.
        var stats = phase == OverlayPhase.Recording && recorded is not null ? recorded with { FrameMs = live?.FrameMs, GpuBusyMs = live?.GpuBusyMs, GpuWaitMs = live?.GpuWaitMs, LatencyMs = live?.LatencyMs } : live;
        var tileList = TileKeys.Where(On).Select(k => Tile(k, stats, load)).ToList();
        var first = items.FirstOrDefault(k => k != Graph && k != Progress);
        var minimal = first is null ? "" : first == Fps ? $"{Whole(live?.Fps)} FPS" : Tile(first, stats, load) is var t ? $"{t.Label} {t.Value}" : "";
        var recording = phase == OverlayPhase.Recording;
        return new OverlayFrame
        {
            Phase = phase, KeyName = keyName, LengthText = Length(plannedSeconds),
            GpuPercent = load?.GpuPercent, CpuPercent = load?.CpuPercent,
            ShowFps = On(Fps), Fps = live?.Fps,
            ShowGraph = recording && On(Graph), Graph = live?.Graph ?? Array.Empty<double>(),
            Tiles = tileList,
            Progress = recording && On(Progress) ? Math.Clamp(elapsedSeconds / Math.Max(1, plannedSeconds), 0, 1) : null,
            Timer = recording && On(Progress) ? $"{Clock(elapsedSeconds)} / {Clock(plannedSeconds)}" : null,
            Footer = recording ? $"{keyName} stop & keep" : $"Press {keyName} to start · {Length(plannedSeconds)}",
            MinimalValue = minimal,
            MinimalTimer = recording ? Clock(elapsedSeconds) : $"{keyName} to start"
        };
    }
}
