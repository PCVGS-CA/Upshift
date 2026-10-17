using System.Text.Json;

namespace Upshift.Core.Measure;

/// <summary>The settings a game had when it was measured.</summary>
public sealed class MeasuredSettings
{
    public string Upscaler { get; set; } = "";
    public string FrameGeneration { get; set; } = "";
    /// <summary>"60 fps", or "No cap".</summary>
    public string FrameCap { get; set; } = "";
    public string Fsr4 { get; set; } = "";
    /// <summary>"OptiScaler v0.9.4", or "OptiScaler not installed by Upshift".</summary>
    public string OptiScaler { get; set; } = "";
    /// <summary>OptiScaler's frame generation was set to something other than off or the game's own.</summary>
    public bool OptiScalerFrameGenOn { get; set; }
}

/// <summary>One measurement of a game.</summary>
public sealed class MeasurementRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Utc { get; set; }
    public double AverageFps { get; set; }
    public double LowFps { get; set; }
    /// <summary>The highest one-second average (FrameStats.MaxFps). Null for runs saved before Upshift 1.3.0.</summary>
    public double? MaxFps { get; set; }
    /// <summary>Frames over twice the median frame time (null for runs measured before this was recorded).</summary>
    public int? Spikes { get; set; }
    public int Frames { get; set; }
    /// <summary>The time the frames covered: the real length of the run.</summary>
    public double Seconds { get; set; }
    /// <summary>The length that was asked for (null for runs saved before Upshift 1.3.0, which were all 60 seconds).</summary>
    public int? PlannedSeconds { get; set; }
    /// <summary>The run was stopped early with the hotkey.</summary>
    public bool StoppedEarly { get; set; }
    public int GeneratedFrames { get; set; }
    /// <summary>The display's refresh rate at the time (for the advice).</summary>
    public int? RefreshHz { get; set; }
    public MeasuredSettings Settings { get; set; } = new();

    /// <summary>The user's note: resolution and in-game settings, which Upshift can't read. Null when there's none.</summary>
    public string? Note { get; set; }

    // Bottleneck details (Upshift 1.3.0). Each is null for older runs, or when its source wasn't available on the PC.

    /// <summary>Per rendered frame, from PresentMon: the game's CPU work, then waiting (FrameTiming).</summary>
    public double? CpuBusyMs { get; set; }
    public double? CpuWaitMs { get; set; }
    /// <summary>Per rendered frame, from PresentMon: the graphics card working on the frame, then idle.</summary>
    public double? GpuBusyMs { get; set; }
    public double? GpuWaitMs { get; set; }
    /// <summary>The average time between the game's own frames, which the busy shares are worked out against.</summary>
    public double? FrameMs { get; set; }
    /// <summary>From Windows' counters (SystemLoad).</summary>
    public double? GpuPercent { get; set; }
    public double? CpuPercent { get; set; }
    public double? BusiestCorePercent { get; set; }
    public long? PeakVramBytes { get; set; }
    public long? TotalVramBytes { get; set; }
    /// <summary>The verdict (BottleneckRules), null when there was nothing to judge by.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BottleneckVerdict>))]
    public BottleneckVerdict? Verdict { get; set; }
    public bool VerdictLessCertain { get; set; }
    public bool VerdictByVSync { get; set; }

    /// <summary>The saved verdict with its texts, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Bottleneck? Bottleneck => Verdict is { } v ? new Bottleneck(v, VerdictLessCertain, VerdictByVSync) : null;

    /// <summary>Frame generation was on: PresentMon saw generated frames, or OptiScaler's frame generation was set.</summary>
    public bool FrameGenOn => GeneratedFrames > 0 || Settings.OptiScalerFrameGenOn;
}

/// <summary>Advice the user applied from a measurement, for the game's change history.</summary>
public sealed class AppliedAdvice
{
    public DateTime Utc { get; set; }
    public string Text { get; set; } = "";
}

public sealed class GameMeasurements
{
    /// <summary>Newest first: the two most recent plus up to MeasureThresholds.OlderRunsKept older ones.</summary>
    public List<MeasurementRun> Runs { get; set; } = new();
    public List<AppliedAdvice> Applied { get; set; } = new();
}

/// <summary>
/// Measurements per game, in measurements\&lt;game&gt;.json in Upshift's data folder. They never leave the PC.
/// </summary>
public sealed class MeasurementStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly object _sync = new();

    public MeasurementStore(string dataDir) => _dir = Path.Combine(dataDir, "measurements");

    private string PathFor(string gameId) =>
        Path.Combine(_dir, string.Concat(gameId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    public GameMeasurements Get(string gameId)
    {
        lock (_sync)
        {
            try
            {
                var path = PathFor(gameId);
                return File.Exists(path) ? JsonSerializer.Deserialize<GameMeasurements>(File.ReadAllText(path), Json) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    public void Add(string gameId, MeasurementRun run) => Change(gameId, m =>
    {
        m.Runs.Insert(0, run);
        var keep = 2 + MeasureThresholds.OlderRunsKept;
        if (m.Runs.Count > keep) m.Runs.RemoveRange(keep, m.Runs.Count - keep);
    });

    public void Delete(string gameId, string runId) => Change(gameId, m => m.Runs.RemoveAll(r => r.Id == runId));

    /// <summary>Saves the user's note on one run (empty clears it).</summary>
    public void SetNote(string gameId, string runId, string? note) => Change(gameId, m =>
    {
        if (m.Runs.FirstOrDefault(r => r.Id == runId) is { } run) run.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    });

    /// <summary>Every game with saved runs: its id and runs (newest first). For "Export all results".</summary>
    public List<(string GameId, List<MeasurementRun> Runs)> All()
    {
        var result = new List<(string, List<MeasurementRun>)>();
        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(_dir)) return result;
                foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    var runs = Get(id).Runs;
                    if (runs.Count > 0) result.Add((id, runs));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public void RecordApplied(string gameId, string text) => Change(gameId, m => m.Applied.Add(new AppliedAdvice { Utc = DateTime.UtcNow, Text = text }));

    /// <summary>Takes back the last applied advice with this text (when applying it failed).</summary>
    public void ForgetApplied(string gameId, string text) => Change(gameId, m =>
    {
        if (m.Applied.LastOrDefault(a => a.Text == text) is { } last) m.Applied.Remove(last);
    });

    private void Change(string gameId, Action<GameMeasurements> change)
    {
        lock (_sync)
        {
            var m = Get(gameId);
            change(m);
            Directory.CreateDirectory(_dir);
            var path = PathFor(gameId);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(m, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}
