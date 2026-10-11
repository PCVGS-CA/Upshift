namespace Upshift.Core.Measure;

public enum BottleneckVerdict { GraphicsCard, Processor, Balanced, FrameCap }

/// <summary>The verdict for one run, with its advice. <see cref="LessCertain"/> when frame generation was on.</summary>
public sealed record Bottleneck(BottleneckVerdict Verdict, bool LessCertain, bool ByVSync)
{
    public string Title => Verdict switch
    {
        BottleneckVerdict.GraphicsCard => "Limited by your graphics card",
        BottleneckVerdict.Processor => "Limited by your processor",
        BottleneckVerdict.FrameCap => "Limited by your frame cap",
        _ => "Balanced"
    };

    public string Advice => Verdict switch
    {
        BottleneckVerdict.GraphicsCard => "Try a lower upscaler quality (Balanced or Performance instead of Quality), or lower graphics settings.",
        BottleneckVerdict.Processor => "Lower graphics settings won't help much here. Frame generation can raise the frame rate when the processor is the limit.",
        BottleneckVerdict.FrameCap => ByVSync
            ? "The game runs at your screen's refresh rate with VSync on, so VSync sets the frame rate, not the hardware."
            : "The game reaches your frame cap, so the cap sets the frame rate. Raise or remove it to see what the PC can do.",
        _ => "Neither part is clearly holding the game back: a lower upscaler quality and frame generation can both help a little."
    };

    public string? Caution => LessCertain
        ? "Less certain: frame generation was on, and generated frames blur where each frame's time goes."
        : null;
}

/// <summary>
/// Works out what limited the frame rate. Thresholds, and why:
/// <list type="bullet">
/// <item>Frame cap first: Upshift's frame cap is set and the average is at least 95% of it, or VSync was on for most
/// frames and the average is at least 95% of the screen's refresh rate. Limiters land 1–3% under their target, and a
/// capped game leaves both the processor and the card idle part of each frame, which would otherwise look like a
/// processor limit.</item>
/// <item>From PresentMon (preferred): the share of each frame the card was busy (MsGPUBusy ÷ the game's frame time).
/// 90% or more: the graphics card. A card that is the limit is never busy for exactly 100% of a frame (short gaps
/// between submissions and presenting take a few per cent), so 90% leaves room for that. 75% or less: the processor:
/// the card sat idle for a quarter of every frame or more, waiting for the next one. In between: balanced.</item>
/// <item>Without PresentMon's busy times, Windows' GPU usage: 95% or more (what Task Manager shows for a card at its
/// limit) is the card; under 80% with one core at 85% or more is the processor (a busy main thread); otherwise balanced
/// unless there's nothing to go on.</item>
/// <item>Frame generation on: the same verdict, marked less certain.</item>
/// </list>
/// </summary>
public static class BottleneckRules
{
    public const double CapReachedShare = 0.95;
    public const double GpuLimitedShare = 0.90;
    public const double CpuLimitedShare = 0.75;
    public const double GpuLimitedPercent = 95;
    public const double GpuIdlePercent = 80;
    public const double BusyCorePercent = 85;
    public const double VSyncMajority = 0.5;

    /// <param name="capFps">Upshift's frame cap for the run, or null for none.</param>
    /// <param name="refreshHz">The screen's refresh rate, for VSync.</param>
    /// <returns>Null when there's nothing to judge by.</returns>
    public static Bottleneck? Judge(double averageFps, FrameTiming? timing, SystemLoad? load, double? capFps, int? refreshHz, bool frameGenOn)
    {
        if (capFps is > 0 && averageFps >= capFps.Value * CapReachedShare)
            return new Bottleneck(BottleneckVerdict.FrameCap, frameGenOn, ByVSync: false);
        if (timing?.VSyncShare is >= VSyncMajority && refreshHz is > 0 && averageFps >= refreshHz.Value * CapReachedShare)
            return new Bottleneck(BottleneckVerdict.FrameCap, frameGenOn, ByVSync: true);

        BottleneckVerdict? verdict = null;
        if (timing is { GpuBusyMs: { } gpuBusy, FrameMs: > 0 })
        {
            var share = gpuBusy / timing.FrameMs;
            verdict = share >= GpuLimitedShare ? BottleneckVerdict.GraphicsCard
                    : share <= CpuLimitedShare ? BottleneckVerdict.Processor
                    : BottleneckVerdict.Balanced;
        }
        else if (load?.GpuPercent is { } gpu)
        {
            verdict = gpu >= GpuLimitedPercent ? BottleneckVerdict.GraphicsCard
                    : gpu < GpuIdlePercent && load.BusiestCorePercent is >= BusyCorePercent ? BottleneckVerdict.Processor
                    : BottleneckVerdict.Balanced;
        }
        return verdict is { } v ? new Bottleneck(v, frameGenOn, ByVSync: false) : null;
    }
}
