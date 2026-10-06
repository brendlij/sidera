using System;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core;
using Sidera.Core.Mounts;
using Sidera.Desktop.Diagnostics;
using Sidera.Desktop.Settings;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// One editable tab of the settings with the behavior they all share: what is typed is not applied until Save; the tab says when it differs from what is saved ("Unsaved changes"); Save checks the
/// values, refuses with one sentence and changes nothing when they are wrong, and says "Saved." when it worked. The same for every tab, so that nobody has to remember which one saves by itself.
/// </summary>
public abstract partial class SettingsSectionViewModel : ViewModelBase
{
    private bool _loading;

    protected SettingsSectionViewModel()
    {
        PropertyChanged += (_, e) =>
        {
            if (!_loading && e.PropertyName is not (nameof(IsDirty) or nameof(ResultText) or nameof(ResultIsProblem) or nameof(DirtyText) or nameof(HasResult)))
            {
                UpdateDirty();
            }
        };
    }

    /// <summary>The fields differ from what is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirtyText))]
    public partial bool IsDirty { get; private set; }

    public string DirtyText => IsDirty ? "Unsaved changes" : string.Empty;

    /// <summary>What the last Save said: "Saved." or the reason it was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ResultIsProblem { get; private set; }

    public bool HasResult => ResultText.Length > 0;

    /// <summary>One sentence for what a saved change does and when ("New workflows use it at once").</summary>
    public virtual string AppliesText => string.Empty;

    /// <summary>Whether the fields as they are differ from what is saved; <c>true</c> when a field does not read.</summary>
    protected abstract bool Differs();

    /// <summary>Reads what is saved into the fields.</summary>
    protected abstract void Reload();

    /// <summary>Checks the fields and saves them; the sentence of the problem when it cannot.</summary>
    protected abstract string? SaveCore();

    /// <summary>Reads the saved values into the fields again (after another part changed them, or to throw the typing away).</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            Reload();
        }
        finally
        {
            _loading = false;
        }

        UpdateDirty();
    }

    protected void Loaded() => Refresh();

    private void UpdateDirty() => IsDirty = Differs();

    [RelayCommand]
    private void Save()
    {
        var problem = SaveCore();
        ResultIsProblem = problem is not null;
        ResultText = problem ?? ("Saved." + (AppliesText.Length > 0 ? " " + AppliesText : string.Empty));
        if (problem is null)
        {
            Refresh();
        }
    }

    /// <summary>Puts the saved values back into the fields and forgets what was typed.</summary>
    [RelayCommand]
    private void Discard()
    {
        Refresh();
        ResultText = string.Empty;
        ResultIsProblem = false;
    }

    // ---- reading what was typed

    protected static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    protected static bool TryNumber(string? text, out double value)
    {
        var t = text?.Trim();
        return (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    }

    protected static bool TryWhole(string? text, out int value)
    {
        var t = text?.Trim();
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) || int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // A field that does not read is a problem of the field, named; null when all do.
    protected static string? Unreadable(params (string Label, bool Ok)[] fields)
    {
        foreach (var (label, ok) in fields)
        {
            if (!ok)
            {
                return $"{label} must be a number.";
            }
        }

        return null;
    }
}

/// <summary>How a new session opens. Changing it never changes a session that exists.</summary>
public sealed partial class SequencerSettingsViewModel : SettingsSectionViewModel
{
    private readonly SiteService _settings;

    public SequencerSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        Loaded();
    }

    /// <summary>A new session opens as a workflow.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvancedDefault))]
    public partial bool IsWorkflowDefault { get; set; }

    /// <summary>A new session opens as the explicit tree of steps.</summary>
    public bool IsAdvancedDefault
    {
        get => !IsWorkflowDefault;
        set => IsWorkflowDefault = !value;
    }

    public override string AppliesText => "The next new session opens in that mode; the one that is open stays as it is.";

    private SequencerSettings Build() => new() { DefaultSessionMode = IsWorkflowDefault ? SessionMode.Workflow : SessionMode.Advanced };

    protected override bool Differs() => Build() != _settings.Sequencer;

    protected override void Reload() => IsWorkflowDefault = _settings.Sequencer.DefaultSessionMode == SessionMode.Workflow;

    protected override string? SaveCore() => _settings.SetSequencer(Build()).Problem;
}

/// <summary>The manual imaging page: where frames are saved, how they are shown, and the exposure a manual capture starts with. The acquisition defaults of a camera stay with the camera.</summary>
public sealed partial class ImagingSettingsViewModel : SettingsSectionViewModel
{
    private readonly SiteService _settings;

    public ImagingSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        Loaded();
    }

    [ObservableProperty] public partial string SaveDirectoryText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AutoStretch { get; set; }
    [ObservableProperty] public partial bool FitOnCapture { get; set; }
    [ObservableProperty] public partial string ManualExposureText { get; set; } = string.Empty;

    public override string AppliesText => "The imaging page uses it from the next start; the save folder at once.";

    private ImagingSettings? Build() =>
        TryNumber(ManualExposureText, out var exposure)
            ? new ImagingSettings
            {
                SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectoryText) ? null : SaveDirectoryText.Trim(), AutoStretch = AutoStretch, FitOnCapture = FitOnCapture, ManualExposureSeconds = exposure,
            }
            : null;

    protected override bool Differs() => Build() is not { } built || built != _settings.Imaging;

    protected override void Reload()
    {
        var s = _settings.Imaging;
        SaveDirectoryText = s.SaveDirectory ?? string.Empty;
        AutoStretch = s.AutoStretch;
        FitOnCapture = s.FitOnCapture;
        ManualExposureText = Format(s.ManualExposureSeconds);
    }

    protected override string? SaveCore() =>
        Unreadable(("The manual exposure", TryNumber(ManualExposureText, out _))) ?? _settings.SetImaging(Build()!).Problem;
}

/// <summary>What an autofocus starts with, and the autofocus policy a new workflow starts with.</summary>
public sealed partial class AutofocusSettingsViewModel : SettingsSectionViewModel
{
    private readonly SiteService _settings;

    public AutofocusSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        Loaded();
    }

    [ObservableProperty] public partial string ExposureText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StepSizeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SamplesText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PolicyEnabled { get; set; }
    [ObservableProperty] public partial bool PolicyAtStart { get; set; }
    [ObservableProperty] public partial string IntervalText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PolicyAfterFilterChange { get; set; }
    [ObservableProperty] public partial bool HoldMountStable { get; set; }

    public override string AppliesText => "New workflows use it at once; a new Autofocus step and the manual autofocus from the next start. After a meridian flip: see the Meridian Flip tab.";

    private AutofocusDefaults? Build() =>
        TryNumber(ExposureText, out var exposure) && TryWhole(StepSizeText, out var step) && TryWhole(SamplesText, out var samples) && TryNumber(IntervalText, out var interval)
            ? new AutofocusDefaults
            {
                ExposureSeconds = exposure, StepSize = step, SampleCount = samples, PolicyEnabled = PolicyEnabled, PolicyAtStart = PolicyAtStart, PolicyIntervalMinutes = interval,
                PolicyAfterFilterChange = PolicyAfterFilterChange, HoldMountStable = HoldMountStable,
            }
            : null;

    protected override bool Differs() => Build() is not { } built || built != _settings.Autofocus;

    protected override void Reload()
    {
        var s = _settings.Autofocus;
        ExposureText = Format(s.ExposureSeconds);
        StepSizeText = s.StepSize.ToString(CultureInfo.InvariantCulture);
        SamplesText = s.SampleCount.ToString(CultureInfo.InvariantCulture);
        PolicyEnabled = s.PolicyEnabled;
        PolicyAtStart = s.PolicyAtStart;
        IntervalText = Format(s.PolicyIntervalMinutes);
        PolicyAfterFilterChange = s.PolicyAfterFilterChange;
        HoldMountStable = s.HoldMountStable;
    }

    protected override string? SaveCore() =>
        Unreadable(
            ("The exposure", TryNumber(ExposureText, out _)), ("The step size", TryWhole(StepSizeText, out _)), ("The number of samples", TryWhole(SamplesText, out _)),
            ("The interval", TryNumber(IntervalText, out _)))
        ?? _settings.SetAutofocus(Build()!).Problem;
}

/// <summary>What a new workflow does about guiding and dithering; a workflow can override each value.</summary>
public sealed partial class GuidingSettingsViewModel : SettingsSectionViewModel
{
    private readonly SiteService _settings;

    public GuidingSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        Loaded();
    }

    [ObservableProperty] public partial bool StartBeforeImaging { get; set; }
    [ObservableProperty] public partial bool StopWhenDone { get; set; }
    [ObservableProperty] public partial bool DitherByDefault { get; set; }
    [ObservableProperty] public partial string EveryText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AmplitudeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ThresholdText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StableText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TimeoutText { get; set; } = string.Empty;

    public override string AppliesText => "New workflows use it at once. The guider is the one of the imaging setup.";

    private GuidingDefaults? Build() =>
        TryWhole(EveryText, out var every) && TryNumber(AmplitudeText, out var amplitude) && TryNumber(ThresholdText, out var threshold) && TryNumber(StableText, out var stable)
        && TryNumber(TimeoutText, out var timeout)
            ? new GuidingDefaults
            {
                StartBeforeImaging = StartBeforeImaging, StopWhenDone = StopWhenDone, DitherByDefault = DitherByDefault, DitherEveryNFrames = every, DitherAmplitudePixels = amplitude,
                SettleThresholdPixels = threshold, SettleStableSeconds = stable, SettleTimeoutSeconds = timeout,
            }
            : null;

    protected override bool Differs() => Build() is not { } built || built != _settings.Guiding;

    protected override void Reload()
    {
        var s = _settings.Guiding;
        StartBeforeImaging = s.StartBeforeImaging;
        StopWhenDone = s.StopWhenDone;
        DitherByDefault = s.DitherByDefault;
        EveryText = s.DitherEveryNFrames.ToString(CultureInfo.InvariantCulture);
        AmplitudeText = Format(s.DitherAmplitudePixels);
        ThresholdText = Format(s.SettleThresholdPixels);
        StableText = Format(s.SettleStableSeconds);
        TimeoutText = Format(s.SettleTimeoutSeconds);
    }

    protected override string? SaveCore() =>
        Unreadable(
            ("Every N frames", TryWhole(EveryText, out _)), ("The dither amount", TryNumber(AmplitudeText, out _)), ("The settle tolerance", TryNumber(ThresholdText, out _)),
            ("The settle time", TryNumber(StableText, out _)), ("The settle timeout", TryNumber(TimeoutText, out _)))
        ?? _settings.SetGuiding(Build()!).Problem;
}

/// <summary>
/// The meridian flip that every workflow follows unless it has settings of its own: the same <see cref="MeridianFlipSettings"/> that a workflow can customize, set once for the application. A
/// workflow that was customized is not changed by what is saved here.
/// </summary>
public sealed partial class MeridianFlipSettingsViewModel : SettingsSectionViewModel
{
    private readonly SiteService _settings;

    public MeridianFlipSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        Loaded();
    }

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string PauseBeforeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FlipAfterText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LatestText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StopGuiding { get; set; }
    [ObservableProperty] public partial bool Recenter { get; set; }
    [ObservableProperty] public partial bool VerifyRotation { get; set; }
    [ObservableProperty] public partial bool Autofocus { get; set; }
    [ObservableProperty] public partial bool RestartGuiding { get; set; }
    [ObservableProperty] public partial bool Dither { get; set; }
    [ObservableProperty] public partial string PauseAfterText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AttemptsText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PauseOnFailure { get; set; }
    [ObservableProperty] public partial string ToleranceText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CenteringAttemptsText { get; set; } = string.Empty;

    /// <summary>An exposure that runs when the flip comes due is let to finish: always, in this version.</summary>
    public bool FinishCurrentExposure => true;

    public override string AppliesText => "Workflows that use the defaults follow them at once. A workflow with its own flip is not changed.";

    private MeridianFlipSettings? Build() =>
        TryNumber(PauseBeforeText, out var before) && TryNumber(FlipAfterText, out var after) && TryNumber(LatestText, out var latest) && TryNumber(PauseAfterText, out var pause)
        && TryWhole(AttemptsText, out var attempts) && TryNumber(ToleranceText, out var tolerance) && TryWhole(CenteringAttemptsText, out var centering)
            ? _settings.MeridianFlip with
            {
                Enabled = Enabled, PauseBeforeMeridianMinutes = before, FlipAfterMeridianMinutes = after, LatestAllowedFlipMinutes = latest, StopGuidingBeforeFlip = StopGuiding,
                RecenterAfterFlip = Recenter, VerifyRotationAfterFlip = VerifyRotation, AutofocusAfterFlip = Autofocus, RestartGuidingAfterFlip = RestartGuiding, DitherAfterFlip = Dither,
                PauseAfterFlipMinutes = pause, MaxFlipAttempts = attempts,
                FailureBehavior = PauseOnFailure ? MeridianFlipFailureBehavior.PauseSession : MeridianFlipFailureBehavior.AbortSession, CenteringToleranceArcseconds = tolerance,
                MaxCenteringAttempts = centering, FinishCurrentExposure = true,
            }
            : null;

    protected override bool Differs() => Build() is not { } built || built != _settings.MeridianFlip;

    protected override void Reload()
    {
        var s = _settings.MeridianFlip;
        Enabled = s.Enabled;
        PauseBeforeText = Format(s.PauseBeforeMeridianMinutes);
        FlipAfterText = Format(s.FlipAfterMeridianMinutes);
        LatestText = Format(s.LatestAllowedFlipMinutes);
        StopGuiding = s.StopGuidingBeforeFlip;
        Recenter = s.RecenterAfterFlip;
        VerifyRotation = s.VerifyRotationAfterFlip;
        Autofocus = s.AutofocusAfterFlip;
        RestartGuiding = s.RestartGuidingAfterFlip;
        Dither = s.DitherAfterFlip;
        PauseAfterText = Format(s.PauseAfterFlipMinutes);
        AttemptsText = s.MaxFlipAttempts.ToString(CultureInfo.InvariantCulture);
        PauseOnFailure = s.FailureBehavior == MeridianFlipFailureBehavior.PauseSession;
        ToleranceText = Format(s.CenteringToleranceArcseconds);
        CenteringAttemptsText = s.MaxCenteringAttempts.ToString(CultureInfo.InvariantCulture);
    }

    protected override string? SaveCore() =>
        Unreadable(
            ("The pause before the meridian", TryNumber(PauseBeforeText, out _)), ("The flip after the meridian", TryNumber(FlipAfterText, out _)),
            ("The latest allowed flip", TryNumber(LatestText, out _)), ("The pause after the flip", TryNumber(PauseAfterText, out _)), ("The attempts", TryWhole(AttemptsText, out _)),
            ("The centering tolerance", TryNumber(ToleranceText, out _)), ("The centering attempts", TryWhole(CenteringAttemptsText, out _)))
        ?? _settings.SetMeridianFlip(Build()!).Problem;
}

/// <summary>
/// The technical side: where the files are, which environment variables the application reads, and the answers given to the questions that come before real equipment moves. Nothing here is
/// edited; "Forget" clears the answers. The variables of the hardware tests (SIDERA_*_OK and the like) are not settings and are not listed.
/// </summary>
public sealed partial class AdvancedSettingsViewModel : ViewModelBase
{
    private readonly HardwareSafetyViewModel? _safety;

    public AdvancedSettingsViewModel(SiteService settings, LogInfo? log, HardwareSafetyViewModel? safety)
    {
        _safety = safety;
        SettingsFileText = settings.FilePath;
        EquipmentFileText = SideraEnvironment.Get("SIDERA_EQUIPMENT_FILE") is { Length: > 0 } custom ? custom : Hardware.EquipmentConfigurationStore.CreateDefault().Path;
        LogFolderText = log?.Directory ?? "—";
        EnvironmentText =
        [
            $"SIDERA_EQUIPMENT_FILE: {SideraEnvironment.Get("SIDERA_EQUIPMENT_FILE") ?? "not set (the default file)"}",
            $"SIDERA_SETTINGS_FILE: {SideraEnvironment.Get("SIDERA_SETTINGS_FILE") ?? "not set (the default file)"}",
        ];
        if (safety is not null)
        {
            safety.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HardwareSafetyViewModel.AnsweredCount))
                {
                    OnPropertyChanged(nameof(AnsweredText));
                }
            };
        }
    }

    public string SettingsFileText { get; }
    public string EquipmentFileText { get; }
    public string LogFolderText { get; }

    /// <summary>The environment variables that the application reads, and their values.</summary>
    public string[] EnvironmentText { get; }

    public bool HasSafety => _safety is not null;

    /// <summary>"Equipment answered for in this run: 2", or that nothing was.</summary>
    public string AnsweredText => _safety is null ? string.Empty : _safety.AnsweredCount == 0
        ? "No equipment has been answered for yet in this run: the first operation that moves a real mount or rotator asks."
        : $"Answered for in this run: {_safety.AnsweredCount} piece(s) of equipment. They are forgotten when Sidera is closed.";

    /// <summary>Forgets the answers: the next operation that moves real equipment asks again.</summary>
    [RelayCommand]
    private void Forget() => _safety?.Forget();
}
