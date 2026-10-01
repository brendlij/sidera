using System;

namespace Astra.Desktop;

/// <summary>Timings and values of the standalone simulator demo. The defaults suit a person watching; tests use short ones.</summary>
public sealed record DemoOptions
{
    public TimeSpan ManualExposure { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SequenceExposure { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SequenceWait { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan SlewDuration { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan GuiderStartDuration { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan GuiderStopDuration { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan GuiderDitherDuration { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Dither amplitude in guide camera pixels.</summary>
    public double DitherAmplitudePixels { get; init; } = 1.5;

    /// <summary>Settle criterion after the dither: guide error at most this many guide camera pixels...</summary>
    public double SettleThresholdPixels { get; init; } = 0.5;

    /// <summary>...continuously for this long...</summary>
    public TimeSpan SettleStableDuration { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>...within this overall time.</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
