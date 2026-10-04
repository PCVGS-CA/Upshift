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
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public int GeneratedFrames { get; set; }
    /// <summary>The display's refresh rate at the time (for the advice).</summary>
    public int? RefreshHz { get; set; }
    public MeasuredSettings Settings { get; set; } = new();

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
