using System;
using System.IO;
using Sidera.Core.Focusing;

namespace Sidera.Desktop.Settings;

/// <summary>How a session is edited: as a workflow (a target, Prepare, Imaging, Finish and their policies) or as the explicit tree of steps.</summary>
public enum SessionMode
{
    Workflow,
    Advanced,
}

/// <summary>What the sequencer starts with. Changing it never changes a session that exists.</summary>
public sealed record SequencerSettings
{
    /// <summary>The mode a new session opens in; a workflow, so that the common case needs no knowledge of the action tree.</summary>
    public SessionMode DefaultSessionMode { get; init; } = SessionMode.Workflow;

    public string? Problem => Enum.IsDefined(DefaultSessionMode) ? null : "Choose a default session mode.";
}

/// <summary>What the manual imaging page starts with: where frames are saved, how they are shown, and the exposure of a manual capture. Device-specific acquisition stays with the camera.</summary>
public sealed record ImagingSettings
{
    /// <summary>The folder that the Save dialogs of the imaging page open in; <c>null</c> for the folder the dialog chooses.</summary>
    public string? SaveDirectory { get; init; }

    /// <summary>The viewer starts with Auto Stretch on (it only changes the display, never the data).</summary>
    public bool AutoStretch { get; init; } = true;

    /// <summary>A new frame is shown fitted to the view; off keeps the zoom and position that were chosen.</summary>
    public bool FitOnCapture { get; init; } = true;

    /// <summary>The exposure a manual capture starts with, in seconds.</summary>
    public double ManualExposureSeconds { get; init; } = 2;

    public string? Problem =>
        !double.IsFinite(ManualExposureSeconds) || ManualExposureSeconds is < 0.001 or > 3600 ? "The manual exposure must be from 0.001 to 3600 seconds."
        : SaveDirectory is { } directory && (directory.Trim().Length == 0 || directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0) ? "The save folder is not a valid path."
        : null;
}

/// <summary>
/// The measurements an autofocus starts with (the manual autofocus, a new Autofocus step) and the autofocus policy that a new workflow starts with. Whether a setup focuses by itself is chosen in the
/// workflow; this only says what is proposed. Nothing here is calibrated automatically.
/// </summary>
public sealed record AutofocusDefaults
{
    public double ExposureSeconds { get; init; } = 1;
    public int StepSize { get; init; } = 400;
    public int SampleCount { get; init; } = 7;

    /// <summary>A setup of a new workflow focuses by itself.</summary>
    public bool PolicyEnabled { get; init; }

    public bool PolicyAtStart { get; init; } = true;

    /// <summary>Focus again after this many minutes; 0 is not by time.</summary>
    public double PolicyIntervalMinutes { get; init; }

    public bool PolicyAfterFilterChange { get; init; }

    public string? Problem =>
        !double.IsFinite(ExposureSeconds) || ExposureSeconds is < 0.001 or > 3600 ? "The autofocus exposure must be from 0.001 to 3600 seconds."
        : StepSize is < 1 or > 100_000 ? "The step size must be from 1 to 100000 focuser steps."
        : SampleCount < AutofocusOptions.MinimumSampleCount || SampleCount > AutofocusOptions.MaximumSampleCount || SampleCount % 2 == 0
            ? $"The number of samples must be odd, from {AutofocusOptions.MinimumSampleCount} to {AutofocusOptions.MaximumSampleCount}."
        : !double.IsFinite(PolicyIntervalMinutes) || PolicyIntervalMinutes is < 0 or > 1440 ? "The autofocus interval must be from 0 to 1440 minutes."
        : PolicyEnabled && !PolicyAtStart && !PolicyAfterFilterChange && PolicyIntervalMinutes <= 0 ? "A workflow that focuses by itself needs a trigger: at the start, an interval, or after a filter change."
        : null;
}

/// <summary>What a new workflow does about guiding and dithering. A workflow can override each value; the guider itself is the one of the imaging setup.</summary>
public sealed record GuidingDefaults
{
    /// <summary>A new workflow starts guiding before it images.</summary>
    public bool StartBeforeImaging { get; init; } = true;

    /// <summary>A new workflow stops guiding when it is done.</summary>
    public bool StopWhenDone { get; init; } = true;

    /// <summary>A new workflow dithers.</summary>
    public bool DitherByDefault { get; init; }

    public int DitherEveryNFrames { get; init; } = 3;
    public double DitherAmplitudePixels { get; init; } = 1.5;
    public double SettleThresholdPixels { get; init; } = 0.5;
    public double SettleStableSeconds { get; init; } = 1;
    public double SettleTimeoutSeconds { get; init; } = 10;

    public string? Problem =>
        DitherEveryNFrames is < 1 or > 1000 ? "Dither every N frames: N must be from 1 to 1000."
        : !Valid(DitherAmplitudePixels, 0.01, 100) ? "The dither amount must be from 0.01 to 100 pixels."
        : !Valid(SettleThresholdPixels, 0.01, 100) ? "The settle tolerance must be from 0.01 to 100 pixels."
        : !Valid(SettleStableSeconds, 0.1, 600) ? "The settle time must be from 0.1 to 600 seconds."
        : !Valid(SettleTimeoutSeconds, 1, 3600) || SettleTimeoutSeconds <= SettleStableSeconds ? "The settle timeout must be longer than the settle time, up to 3600 seconds."
        : null;

    private static bool Valid(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
