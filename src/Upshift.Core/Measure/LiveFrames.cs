using System.Globalization;
using System.Text;

namespace Upshift.Core.Measure;

/// <summary>What the overlay shows: the last second's frame rate, the 1% low so far, and the recent frame time.</summary>
public sealed record LiveSnapshot(int Frames, double? Fps, double? LowFps, double? FrameMs, bool ExclusiveFullscreen);

/// <summary>
/// Follows PresentMon's CSV while it's still being written (the same capture Measure saves), for the overlay. Only the
/// game's process is counted, and only its busiest swap chain, as <see cref="PresentMonCsv.Read"/> does. Never throws.
/// </summary>
public sealed class LiveFrames
{
    private const int BinsPerMs = 10, MaxMs = 1000;
    private readonly string _csv;
    private readonly int _processId;
    private long _position;
    private readonly StringBuilder _partial = new();
    private List<string>? _columns;
    private int _pidCol = -1, _swapCol = -1, _timeCol = -1, _modeCol = -1;
    private readonly Dictionary<string, Chain> _chains = new();

    private sealed class Chain
    {
        public int Frames;
        public readonly int[] Counts = new int[MaxMs * BinsPerMs + 1];
        public readonly double[] Sums = new double[MaxMs * BinsPerMs + 1];
        public readonly Queue<double> LastSecond = new();
        public double LastSecondMs;
        public readonly Queue<double> Recent = new();
        public string? Mode;
    }

    public LiveFrames(string csvPath, int processId)
    {
        _csv = csvPath;
        _processId = processId;
    }

    /// <summary>Reads what was added since the last call and returns the current figures.</summary>
    public LiveSnapshot Poll()
    {
        try { ReadNew(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var chain = _chains.Values.OrderByDescending(c => c.Frames).FirstOrDefault();
        if (chain is null || chain.Frames == 0) return new LiveSnapshot(0, null, null, null, false);
        double? fps = chain.LastSecondMs > 0 ? chain.LastSecond.Count * 1000.0 / chain.LastSecondMs : null;
        return new LiveSnapshot(chain.Frames, fps, LowFps(chain), chain.Recent.Count == 0 ? null : chain.Recent.Average(), IsExclusive(chain.Mode));
    }

    /// <summary>
    /// PresentMon's PresentMode for a game in exclusive fullscreen: "Hardware: Legacy Flip" or "Hardware: Legacy Copy to
    /// front buffer". Borderless and windowed games are composed or use independent flip, where an overlay can show.
    /// </summary>
    public static bool IsExclusive(string? presentMode) =>
        presentMode is not null && presentMode.Contains("Legacy", StringComparison.OrdinalIgnoreCase)
                                && presentMode.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase);

    private void ReadNew()
    {
        if (!File.Exists(_csv)) return;
        using var stream = new FileStream(_csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position) { _position = 0; _partial.Clear(); }   // a new file
        if (stream.Length == _position) return;
        stream.Seek(_position, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(stream.Length - _position, 4 * 1024 * 1024)];
        var read = stream.Read(buffer, 0, buffer.Length);
        _position += read;
        _partial.Append(Encoding.UTF8.GetString(buffer, 0, read));
        var text = _partial.ToString();
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
    }

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
        var bin = (int)Math.Min(MaxMs * BinsPerMs, Math.Round(ms * BinsPerMs));
        chain.Counts[bin]++;
        chain.Sums[bin] += ms;
        chain.LastSecond.Enqueue(ms);
        chain.LastSecondMs += ms;
        while (chain.LastSecondMs - chain.LastSecond.Peek() >= 1000 && chain.LastSecond.Count > 1) chain.LastSecondMs -= chain.LastSecond.Dequeue();
        chain.Recent.Enqueue(ms);
        if (chain.Recent.Count > 10) chain.Recent.Dequeue();
        if (_modeCol >= 0 && _modeCol < cells.Length) chain.Mode = cells[_modeCol].Trim();
    }

    /// <summary>The slowest 1% of frames so far (at least one), averaged, as a frame rate: the same rule as the saved result.</summary>
    private static double? LowFps(Chain chain)
    {
        if (chain.Frames < 10) return null;
        var want = Math.Max(1, chain.Frames / 100);
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
