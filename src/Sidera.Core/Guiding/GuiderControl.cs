using Sidera.Core.Devices;

namespace Sidera.Core.Guiding;

/// <summary>What a guider can do, as it said or as the adapter implements reliably. Nothing here names a backend.</summary>
public sealed record GuiderCapabilities
{
    public DriverMetadata Driver { get; init; } = new();

    /// <summary>It can start and stop guiding.</summary>
    public bool CanGuide { get; init; } = true;

    public bool CanDither { get; init; }

    /// <summary>It reports when guiding has settled after a dither or a start.</summary>
    public bool CanSettle { get; init; }

    public bool CanPause { get; init; }

    /// <summary>It delivers a guide measurement for each guide step.</summary>
    public bool ProvidesGuideTelemetry { get; init; }
}

/// <summary>How a settle after a dither or the start of guiding ended.</summary>
public enum GuidingSettleOutcome
{
    /// <summary>No settle has ended yet.</summary>
    None,

    Settled,

    /// <summary>The guide error did not stay within the tolerance before the timeout.</summary>
    TimedOut,

    /// <summary>The guider reported that it could not settle (star lost, guiding stopped, an error).</summary>
    Failed,
}

/// <summary>
/// Where a settle stands: what was asked for and what the guider measures. The distances are in pixels of the guide camera; the durations are
/// as the guider reports them.
/// </summary>
/// <param name="IsActive">A settle is running.</param>
/// <param name="DistancePixels">The current distance of the guide star from its lock position.</param>
/// <param name="StableFor">How long the distance has been within the tolerance.</param>
/// <param name="TolerancePixels">The tolerance that was asked for.</param>
/// <param name="RequiredStable">How long it has to stay within the tolerance.</param>
/// <param name="Timeout">The time limit.</param>
/// <param name="Elapsed">Since the settle began.</param>
/// <param name="LastOutcome">How the last settle ended.</param>
/// <param name="LastDuration">How long the last settle took.</param>
/// <param name="FailureReason">Why the last settle failed, in words.</param>
public sealed record GuidingSettleStatus(
    bool IsActive,
    double? DistancePixels,
    TimeSpan? StableFor,
    double? TolerancePixels,
    TimeSpan? RequiredStable,
    TimeSpan? Timeout,
    TimeSpan Elapsed,
    GuidingSettleOutcome LastOutcome,
    TimeSpan? LastDuration,
    string? FailureReason)
{
    public static GuidingSettleStatus None { get; } =
        new(false, null, null, null, null, null, TimeSpan.Zero, GuidingSettleOutcome.None, null, null);
}

/// <summary>The measurements a guider offers besides the samples. A value the guider does not know is <c>null</c>.</summary>
public sealed record GuidingTelemetry
{
    /// <summary>Rolling RMS over the recent samples, in arcseconds.</summary>
    public GuidingRms Rms { get; init; } = new(null, null, null, 0);

    public double? StarSnr { get; init; }

    public double? StarMass { get; init; }

    /// <summary>The exposure of the guide camera, in seconds.</summary>
    public double? ExposureSeconds { get; init; }

    /// <summary>Arcseconds per pixel of the guide camera; what turns pixels into arcseconds.</summary>
    public double? PixelScaleArcsecPerPixel { get; init; }

    public GuidingSettleStatus Settle { get; init; } = GuidingSettleStatus.None;

    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>What a guider says about the setup it guides with. Whatever it does not say is <c>null</c>.</summary>
public sealed record GuiderInfo(string? Profile, string? GuideCamera, string? Mount, bool? IsCalibrated, bool? EquipmentConnected);

/// <summary>
/// A guider with measurements: its state, a bounded history of guide samples for a graph, the settle it is in and pause when it can.
/// Optional beside <see cref="IGuider"/>.
/// </summary>
public interface IGuiderControl : IGuider, ICapable<GuiderCapabilities>, IObservableDevice
{
    /// <summary>The latest measurements; <c>null</c> while the guider is not connected.</summary>
    GuidingTelemetry? Telemetry { get; }

    /// <summary>The recent guide samples. Cleared when a new connection is made.</summary>
    GuidingHistory History { get; }

    /// <summary>What the guider reports about its setup; <c>null</c> while not connected.</summary>
    GuiderInfo? Info { get; }

    /// <summary>Pauses guiding: the guider keeps its star and does not correct. Only when <see cref="GuiderCapabilities.CanPause"/>.</summary>
    Task PauseGuidingAsync(CancellationToken cancellationToken = default);

    /// <summary>Resumes paused guiding.</summary>
    Task ResumeGuidingAsync(CancellationToken cancellationToken = default);
}
