using System;
using System.IO;
using System.Linq;
using Sidera.Core.Focusing;

namespace Sidera.Desktop.Settings;

/// <summary>How a session is edited: as a workflow (a target, Prepare, Imaging, Finish and their policies) or as the explicit tree of steps.</summary>
public enum SessionMode
{
    Workflow,
    Advanced,
}

/// <summary>How Sidera looks: the theme (see <c>SideraThemes</c>) and which panels the dashboard shows. Changing it changes nothing but what is drawn.</summary>
public sealed record AppearanceSettings
{
    /// <summary>The id of the theme; one of those of <c>SideraThemes</c>.</summary>
    public string ThemeId { get; init; } = Sidera.Desktop.Themes.SideraThemes.DefaultId;

    /// <summary>The imaging setups (or cameras) with what each of them is doing.</summary>
    public bool DashboardSetups { get; init; } = true;

    /// <summary>The mount: state, position, pier side, meridian.</summary>
    public bool DashboardMount { get; init; } = true;

    /// <summary>The guide graph and RMS, when the guider measures.</summary>
    public bool DashboardGuiding { get; init; } = true;

    /// <summary>The latest image with its analysis.</summary>
    public bool DashboardImage { get; init; } = true;

    /// <summary>The strip with the mount and guider that the session shares.</summary>
    public bool DashboardShared { get; init; } = true;

    public string? Problem => Sidera.Desktop.Themes.SideraThemes.Find(ThemeId) is null
        ? "Choose one of the themes: " + string.Join(", ", Sidera.Desktop.Themes.SideraThemes.All.Select(t => t.Id)) + "."
        : null;
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

    /// <summary>Every frame that a session takes is written to disk as FITS, in the save folder (or the default one when there is none).</summary>
    public bool SaveSessionFrames { get; init; } = true;

    /// <summary>The folders inside the save folder, as a pattern of parts in braces (see <see cref="Sidera.Core.Imaging.FrameNaming"/>); empty saves into the save folder itself.</summary>
    public string FolderPattern { get; init; } = Sidera.Core.Imaging.FrameNaming.DefaultFolderPattern;

    /// <summary>The name of the file, without its extension, as a pattern.</summary>
    public string FilePattern { get; init; } = Sidera.Core.Imaging.FrameNaming.DefaultFilePattern;

    /// <summary>The folder that session frames go to: the save folder, or the Sidera folder in the Pictures of the user when none is chosen.</summary>
    public string FramesRoot => SaveDirectory is { Length: > 0 } directory ? directory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sidera");

    public string? Problem =>
        !double.IsFinite(ManualExposureSeconds) || ManualExposureSeconds is < 0.001 or > 3600 ? "The manual exposure must be from 0.001 to 3600 seconds."
        : SaveDirectory is { } directory && (directory.Trim().Length == 0 || directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0) ? "The save folder is not a valid path."
        : Sidera.Core.Imaging.FrameNaming.Problem(FolderPattern, isFile: false) ?? Sidera.Core.Imaging.FrameNaming.Problem(FilePattern, isFile: true);
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

    /// <summary>
    /// A focus run holds the stability of the mount, so that the exposures of other cameras on the same mount finish before it starts and none starts until it is over. Off by default: focusing
    /// touches only the camera and the focuser of its setup, and the other setups go on exposing.
    /// </summary>
    public bool HoldMountStable { get; init; }

    /// <summary>The focus is searched for (it starts where the focuser stands, turns round when the stars get larger, and does not fail on a blurred sample) instead of a fixed pattern.</summary>
    public bool SearchForFocus { get; init; } = true;

    /// <summary>A second pass with a quarter of the step size around what the first pass found: the first finds the focus roughly, the second exactly.</summary>
    public bool FinePass { get; init; } = true;

    /// <summary>How far from where it started the search may go, in focuser steps, to either side; 0 is ten steps of the step size.</summary>
    public int SearchRangeSteps { get; init; }

    /// <summary>The curve is fitted as a hyperbola (the default) or as the lines of the two sides.</summary>
    public FocusFitMethod FitMethod { get; init; } = FocusFitMethod.Hyperbola;

    /// <summary>The most samples a search takes; 0 lets the search decide.</summary>
    public int MaxSamples { get; init; }

    /// <summary>How many samples in a row that cannot be measured a search puts up with.</summary>
    public int MaxFailedSamples { get; init; } = 3;

    /// <summary>A search that finds no star where the focuser stands goes to the position where focus was found last.</summary>
    public bool UseRememberedPosition { get; init; } = true;

    /// <summary>What autofocus does with these settings, as the strategy of a run.</summary>
    public AutofocusStrategy Strategy(IFocusHistory? history) => new()
    {
        Mode = SearchForFocus ? AutofocusMode.Search : AutofocusMode.Pattern,
        FitMethod = FitMethod,
        FinePass = FinePass,
        MaxTravelSteps = SearchRangeSteps,
        MaxSamples = MaxSamples,
        MaxFailedSamples = MaxFailedSamples,
        UseRememberedPosition = UseRememberedPosition,
        History = history,
    };

    public string? Problem =>
        !double.IsFinite(ExposureSeconds) || ExposureSeconds is < 0.001 or > 3600 ? "The autofocus exposure must be from 0.001 to 3600 seconds."
        : StepSize is < 1 or > 100_000 ? "The step size must be from 1 to 100000 focuser steps."
        : SampleCount < AutofocusOptions.MinimumSampleCount || SampleCount > AutofocusOptions.MaximumSampleCount || SampleCount % 2 == 0
            ? $"The number of samples must be odd, from {AutofocusOptions.MinimumSampleCount} to {AutofocusOptions.MaximumSampleCount}."
        : MaxSamples != 0 && (MaxSamples < SampleCount || MaxSamples > 100) ? "The most samples of a search must be 0 (the search decides) or from the number of samples to 100."
        : SearchRangeSteps is < 0 or > 1_000_000 ? "The search range must be 0 (automatic) or from 1 to 1000000 focuser steps."
        : MaxFailedSamples is < 0 or > 10 ? "The samples that may fail in a row must be from 0 to 10."
        : !double.IsFinite(PolicyIntervalMinutes) || PolicyIntervalMinutes is < 0 or > 1440 ? "The autofocus interval must be from 0 to 1440 minutes."
        : PolicyEnabled && !PolicyAtStart && !PolicyAfterFilterChange && PolicyIntervalMinutes <= 0 ? "A block that focuses by itself needs a trigger: at the start, an interval, or after a filter change."
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
