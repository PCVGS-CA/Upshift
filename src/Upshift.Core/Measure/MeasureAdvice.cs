namespace Upshift.Core.Measure;

/// <summary>Every number "What to try" uses, in one place.</summary>
public static class MeasureThresholds
{
    /// <summary>How long a measurement runs.</summary>
    public const int MeasureSeconds = 60;
    /// <summary>Older runs kept per game, besides the two most recent.</summary>
    public const int OlderRunsKept = 10;
    /// <summary>The caps "What to try" offers: at or just under the low points, never above the display's rate.</summary>
    public static readonly int[] CommonCaps = { 30, 40, 48, 60, 72, 90, 120 };
    /// <summary>An average under this is "well under 60": try a lower upscaler quality step.</summary>
    public const double WellUnder60 = 50;
    /// <summary>An average above refresh × this is "far above the display": try a higher quality step.</summary>
    public const double FarAboveRefreshFactor = 1.3;
    /// <summary>Frame generation is worth trying from about this average (without it).</summary>
    public const double FrameGenWorthTryingFrom = 57;
    /// <summary>Below this average, frame generation is likely to feel worse.</summary>
    public const double FrameGenFeelsWorseBelow = 45;
    /// <summary>Low points under average × this mean the game is uneven.</summary>
    public const double UnevenLowRatio = 0.6;
}

public enum AdviceKind { FrameCap, LowerQuality, HigherQuality, TryFrameGen, FrameGenWorse, Uneven }

/// <summary>One "What to try" line; FrameCap carries the cap to apply.</summary>
public sealed record AdviceItem(AdviceKind Kind, string Text, int? Cap = null);

public static class MeasureAdvice
{
    /// <summary>The cap for these low points: the largest common cap at or under them and the display's rate.</summary>
    public static int? CapFor(double lowFps, int? refreshHz) =>
        MeasureThresholds.CommonCaps.Where(c => c <= Math.Round(lowFps) && (refreshHz is not { } hz || c <= hz))
            .Select(c => (int?)c).LastOrDefault();

    /// <summary>"What to try" for a run, most useful first.</summary>
    public static List<AdviceItem> For(MeasurementRun run, int? refreshHz, double? currentCap)
    {
        var list = new List<AdviceItem>();
        var avg = run.AverageFps;
        var low = run.LowFps;

        var uneven = low < avg * MeasureThresholds.UnevenLowRatio;
        if (CapFor(low, refreshHz) is { } cap)
        {
            var already = currentCap is { } c && Math.Abs(c - cap) < 0.5;
            var why = uneven
                ? $"The game is uneven: its low points ({Math.Round(low)} fps) are far below its average ({Math.Round(avg)} fps). A frame cap of {cap} fps will smooth it."
                : $"A frame cap of {cap} fps, at or just under the low points ({Math.Round(low)} fps), keeps motion steady.";
            list.Add(new AdviceItem(AdviceKind.FrameCap, already ? why + " It's already set." : why, already ? null : cap));
        }
        else if (uneven)
            list.Add(new AdviceItem(AdviceKind.Uneven,
                $"The game is uneven: its low points ({Math.Round(low)} fps) are far below its average ({Math.Round(avg)} fps). A frame cap would smooth it once the low points reach at least {MeasureThresholds.CommonCaps[0]} fps."));

        if (avg < MeasureThresholds.WellUnder60)
            list.Add(new AdviceItem(AdviceKind.LowerQuality,
                "The average is well under 60 fps. Try the next lower upscaler quality step (for example Quality → Balanced). That's set in the game's own graphics menu; Upshift can't change it."));
        else if (refreshHz is { } hz && avg > hz * MeasureThresholds.FarAboveRefreshFactor)
            list.Add(new AdviceItem(AdviceKind.HigherQuality,
                $"The average is far above your {hz} Hz display. Try a higher upscaler quality step (for example Balanced → Quality, or native/DLAA) in the game's own graphics menu for a sharper picture."));

        if (!run.FrameGenOn)
        {
            if (avg >= MeasureThresholds.FrameGenWorthTryingFrom)
                list.Add(new AdviceItem(AdviceKind.TryFrameGen,
                    "Frame generation is worth trying: the game already runs at about 60 fps or more without it. Pick a mode under Frame generation in OptiScaler options above, then measure again."));
            else if (avg < MeasureThresholds.FrameGenFeelsWorseBelow)
                list.Add(new AdviceItem(AdviceKind.FrameGenWorse,
                    $"Frame generation is likely to feel worse at this frame rate (under about {MeasureThresholds.FrameGenFeelsWorseBelow:0} fps): it adds input lag and needs a steady base frame rate."));
        }
        return list;
    }
}
