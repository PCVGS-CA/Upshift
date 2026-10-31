using System.Globalization;
using System.Text;

namespace Upshift.Core.Measure;

/// <summary>What the overlay shows: the last second's frame rate, the 1% low so far, and the recent frame time.</summary>
public sealed record LiveSnapshot(int Frames, double? Fps, double? LowFps, double? FrameMs, bool ExclusiveFullscreen)
{
    /// <summary>The last ~5 seconds of frame times (ms), oldest first, for the overlay's graph.</summary>
    public IReadOnlyList<double> Graph { get; init; } = Array.Empty<double>();
    /// <summary>All frames read so far over their time, as a frame rate.</summary>
    public double? AverageFps { get; init; }
    /// <summary>The slowest 0.1% of frames so far, averaged, as a frame rate (null under 100 frames).</summary>
    public double? Low01Fps { get; init; }
    /// <summary>The best whole one-second frame count so far (<see cref="PresentMonCsv.MaxOneSecondFps"/>'s rule).</summary>
    public double? MaxFps { get; init; }
    /// <summary>PresentMon's own latency over the last ~5 s (PC latency, else input to photon); null when it reports none.</summary>
    public double? LatencyMs { get; init; }
    /// <summary>PresentMon's GPU busy and GPU wait per frame over the last second; null when it reports none.</summary>
    public double? GpuBusyMs { get; init; }
    public double? GpuWaitMs { get; init; }
}

/// <summary>
/// Follows the capture CSV while it's still being written (Upshift writes it from PresentMon's output), for the overlay.
/// Only the game's process is counted, and only its busiest swap chain, as <see cref="PresentMonCsv.Read"/> does. Never throws.
/// </summary>
public sealed class LiveFrames
{
    private const int BinsPerMs = 10, MaxMs = 1000;
    private readonly string _csv;
    private readonly int _processId;
    private readonly long _startAt;
    private long _position;
    private bool _skipToLineEnd;
    private readonly StringBuilder _partial = new();
    private List<string>? _columns;
    private int _pidCol = -1, _swapCol = -1, _timeCol = -1, _modeCol = -1, _busyCol = -1, _waitCol = -1;
    private int[] _latencyCols = Array.Empty<int>();
    private readonly Dictionary<string, Chain> _chains = new();

    private readonly record struct Extra(double Ms, double? Busy, double? Wait);

    private sealed class Chain
    {
        public int Frames;
        public double TotalMs;
        public readonly int[] Counts = new int[MaxMs * BinsPerMs + 1];
        public readonly double[] Sums = new double[MaxMs * BinsPerMs + 1];
        public readonly Queue<Extra> LastSecond = new();
        public double LastSecondMs;
        public readonly Queue<double> Recent = new();
        public readonly Queue<double> Graph = new();
        public double GraphMs;
        public readonly Queue<(double Ms, double Latency)> Latency = new();
        /// <summary>Frames in a row PresentMon reported in exclusive fullscreen (see <see cref="IsExclusive"/>).</summary>
        public int ExclusiveRun;
        /// <summary>For the max: the whole second the clock is in, its frame count, and the best finished second.</summary>
        public int Second, SecondFrames, BestSecond;
    }

    /// <summary>Graph length: about this many milliseconds of frames.</summary>
    public const double GraphWindowMs = 5000;

    /// <summary>
    /// Exclusive fullscreen only counts after this many frames in a row: a game switching modes, or a single odd frame,
    /// never takes the overlay down.
    /// </summary>
    public const int ExclusiveFrames = 30;

    /// <param name="startAt">
    /// Count only frames written after this point of the file (the measurement's start in a capture that began earlier);
    /// the header is still read from the start, and a line cut at that point is skipped.
    /// </param>
    public LiveFrames(string csvPath, int processId, long startAt = 0)
    {
        _csv = csvPath;
        _processId = processId;
        _startAt = startAt;
    }

    /// <summary>Reads what was added since the last call and returns the current figures.</summary>
    public LiveSnapshot Poll()
    {
        try { ReadNew(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var chain = _chains.Values.OrderByDescending(c => c.Frames).FirstOrDefault();
        if (chain is null || chain.Frames == 0) return new LiveSnapshot(0, null, null, null, false);
        double? fps = chain.LastSecondMs > 0 ? chain.LastSecond.Count * 1000.0 / chain.LastSecondMs : null;
        double? Avg(IEnumerable<double?> values) => values.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Average() is var a && !double.IsNaN(a) ? a : null;
        return new LiveSnapshot(chain.Frames, fps, Low(chain, 100, 10), chain.Recent.Count == 0 ? null : chain.Recent.Average(), chain.ExclusiveRun >= ExclusiveFrames)
        {
            Graph = chain.Graph.ToArray(),
            AverageFps = chain.TotalMs > 0 ? chain.Frames * 1000.0 / chain.TotalMs : null,
            Low01Fps = Low(chain, 1000, 100),
            MaxFps = chain.BestSecond > 0 ? chain.BestSecond : null,
            LatencyMs = chain.Latency.Count > 0 ? chain.Latency.Average(l => l.Latency) : null,
            GpuBusyMs = Avg(chain.LastSecond.Select(e => e.Busy)),
            GpuWaitMs = Avg(chain.LastSecond.Select(e => e.Wait))
        };
    }

    /// <summary>
    /// PresentMon's PresentMode for a game in exclusive fullscreen: "Hardware: Legacy Flip" or "Hardware: Legacy Copy to
    /// front buffer". Borderless and windowed games are composed or use independent flip, where an overlay can show.
    /// </summary>
    public static bool IsExclusive(string? presentMode) =>
        presentMode is not null && presentMode.Contains("Legacy", StringComparison.OrdinalIgnoreCase)
                                && presentMode.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's length now (where a measurement starts or ends in a running capture); 0 when it isn't there yet.</summary>
    public static long Length(string csv)
    {
        try { return File.Exists(csv) ? new FileInfo(csv).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void ReadNew()
    {
        if (!File.Exists(_csv)) return;
        using var stream = new FileStream(_csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position) { _position = 0; _partial.Clear(); }   // a new file
        if (_columns is null && _startAt > 0 && _position == 0)
        {
            // The header first, then on to where counting starts.
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true))
                if (reader.ReadLine() is { } header) Header(header); else return;
            _position = _startAt;
            _skipToLineEnd = true;
        }
        if (stream.Length <= _position) return;
        stream.Seek(_position, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(stream.Length - _position, 4 * 1024 * 1024)];
        var read = stream.Read(buffer, 0, buffer.Length);
        _position += read;
        _partial.Append(Encoding.UTF8.GetString(buffer, 0, read));
        var text = _partial.ToString();
        if (_skipToLineEnd)
        {
            var end = text.IndexOf('\n');
            if (end < 0) { _partial.Clear(); return; }
            text = text[(end + 1)..];
            _partial.Clear().Append(text);
            _skipToLineEnd = false;
        }
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0) return;
        _partial.Clear().Append(text[(lastBreak + 1)..]);   // an unfinished line waits for the rest
        foreach (var raw in text[..lastBreak].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (_columns is null) { Header(line); continue; }
            Row(line);
        }
    }

    private void Header(string line)
    {
        _columns = line.Split(',').Select(c => c.Trim().TrimStart('﻿')).ToList();
        int Col(params string[] names) => names.Select(n => _columns.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
        _pidCol = Col("ProcessID");
        _swapCol = Col("SwapChainAddress");
        _timeCol = Col("MsBetweenPresents", "FrameTime");
        _modeCol = Col("PresentMode");
        _busyCol = Col("MsGPUBusy", "GPUBusy");
        _waitCol = Col("MsGPUWait", "GPUWait");
        // PresentMon's latency columns, best first: PC latency (games with Reflex markers), then input to photon.
        _latencyCols = new[] { Col("MsPCLatency", "PCLatency"), Col("MsAllInputToPhotonLatency", "AllInputToPhotonLatency") }.Where(i => i >= 0).ToArray();
    }

    private static double? Number(string[] cells, int col) =>
        col >= 0 && col < cells.Length && double.TryParse(cells[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 5000 ? v : null;

    private void Row(string line)
    {
        if (_timeCol < 0) return;
        var cells = line.Split(',');
        if (cells.Length <= _timeCol) return;
        if (_pidCol >= 0 && (!int.TryParse(cells[_pidCol], out var pid) || pid != _processId)) return;
        if (!double.TryParse(cells[_timeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 5000) return;
        var key = _swapCol >= 0 && _swapCol < cells.Length ? cells[_swapCol] : "";
        if (!_chains.TryGetValue(key, out var chain)) _chains[key] = chain = new Chain();
        chain.Frames++;
        chain.TotalMs += ms;
        var bin = (int)Math.Min(MaxMs * BinsPerMs, Math.Round(ms * BinsPerMs));
        chain.Counts[bin]++;
        chain.Sums[bin] += ms;
        chain.LastSecond.Enqueue(new Extra(ms, Number(cells, _busyCol), Number(cells, _waitCol)));
        chain.LastSecondMs += ms;
        while (chain.LastSecondMs - chain.LastSecond.Peek().Ms >= 1000 && chain.LastSecond.Count > 1) chain.LastSecondMs -= chain.LastSecond.Dequeue().Ms;
        chain.Recent.Enqueue(ms);
        if (chain.Recent.Count > 10) chain.Recent.Dequeue();
        chain.Graph.Enqueue(ms);
        chain.GraphMs += ms;
        while (chain.GraphMs > GraphWindowMs && chain.Graph.Count > 1) chain.GraphMs -= chain.Graph.Dequeue();
        // Latency: the first column with a number on this frame; kept for the graph's window (input-to-photon only has a
        // value on frames with input).
        foreach (var col in _latencyCols)
            if (Number(cells, col) is { } latency) { chain.Latency.Enqueue((chain.TotalMs, latency)); break; }
        while (chain.Latency.Count > 0 && chain.TotalMs - chain.Latency.Peek().Ms > GraphWindowMs) chain.Latency.Dequeue();
        // Max: frames per whole second of the clock, the same buckets as the saved result.
        var second = (int)(chain.TotalMs / 1000.0);
        if (second != chain.Second)
        {
            chain.BestSecond = Math.Max(chain.BestSecond, chain.SecondFrames);
            chain.Second = second;
            chain.SecondFrames = 0;
        }
        chain.SecondFrames++;
        chain.ExclusiveRun = _modeCol >= 0 && _modeCol < cells.Length && IsExclusive(cells[_modeCol].Trim()) ? chain.ExclusiveRun + 1 : 0;
    }

    /// <summary>
    /// A measurement cut from a capture: the header plus the whole lines written between two points of the file, as a CSV
    /// of its own (for <see cref="PresentMonCsv.Read"/>). Lines cut at either point are left out.
    /// </summary>
    public static void Slice(string csv, long from, long to, string output)
    {
        using var stream = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: true);
        var header = reader.ReadLine() ?? "";
        to = Math.Min(to, stream.Length);
        if (to <= from) { File.WriteAllText(output, header + "\r\n"); return; }
        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[to - from];
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer);
        var first = from == 0 ? 0 : text.IndexOf('\n') + 1;
        var last = text.LastIndexOf('\n');
        var body = first > 0 && last >= first ? text[first..(last + 1)] : first == 0 && last >= 0 ? text[..(last + 1)] : "";
        if (from == 0) body = body[(body.IndexOf('\n') + 1)..];   // the header is already written
        File.WriteAllText(output, header + "\r\n" + body);
    }

    /// <summary>The slowest 1/<paramref name="per"/> of frames so far (at least one), averaged, as a frame rate: the same rule as the saved result.</summary>
    private static double? Low(Chain chain, int per, int minFrames)
    {
        if (chain.Frames < minFrames) return null;
        var want = Math.Max(1, chain.Frames / per);
        var taken = 0;
        double sum = 0;
        for (var bin = chain.Counts.Length - 1; bin >= 0 && taken < want; bin--)
        {
            var n = chain.Counts[bin];
            if (n == 0) continue;
            var use = Math.Min(n, want - taken);
            sum += chain.Sums[bin] / n * use;
            taken += use;
        }
        return taken == 0 || sum <= 0 ? null : 1000.0 / (sum / taken);
    }
}
