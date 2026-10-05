using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

public sealed partial class RotateToAngleStepDraftViewModel : StepDraftViewModel
{
    public RotateToAngleStepDraftViewModel(RotateToAngleStepDraft draft, RigPickerViewModel rig) : base(draft.Id)
    {
        Rig = rig;
        rig.Changed += (_, _) => NotifyEdited();
        AngleText = Format(draft.SkyRotationDegrees);
    }

    public override SequenceStepKind Kind => SequenceStepKind.RotateToAngle;
    public RigPickerViewModel Rig { get; }
    [ObservableProperty] public partial string AngleText { get; set; } = "0";
    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];
    internal override SequenceStepDraft Read(List<string> parseErrors) => new RotateToAngleStepDraft(
        Id, Rig.SelectedId, ParseNumber(AngleText, "Sky rotation", "a number of degrees", parseErrors, 0));
}

public sealed partial class RotateAndVerifyStepDraftViewModel : StepDraftViewModel
{
    public RotateAndVerifyStepDraftViewModel(RotateAndVerifyStepDraft draft, RigPickerViewModel rig) : base(draft.Id)
    {
        Rig = rig;
        rig.Changed += (_, _) => NotifyEdited();
        AngleText = Format(draft.SkyRotationDegrees);
        ToleranceText = Format(draft.ToleranceDegrees);
        MaxAttemptsText = Format(draft.MaxAttempts);
        ExposureText = Format(draft.ExposureSeconds);
    }

    public override SequenceStepKind Kind => SequenceStepKind.RotateAndVerify;
    public RigPickerViewModel Rig { get; }
    [ObservableProperty] public partial string AngleText { get; set; } = "0";
    [ObservableProperty] public partial string ToleranceText { get; set; } = "0.5";
    [ObservableProperty] public partial string MaxAttemptsText { get; set; } = "4";
    [ObservableProperty] public partial string ExposureText { get; set; } = "5";
    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];
    internal override SequenceStepDraft Read(List<string> parseErrors) => new RotateAndVerifyStepDraft(
        Id, Rig.SelectedId,
        ParseNumber(AngleText, "Sky rotation", "a number of degrees", parseErrors, 0),
        ParseNumber(ToleranceText, "Tolerance", "a number of degrees", parseErrors, Sidera.Runtime.Astrometry.RotationService.DefaultToleranceDegrees),
        (int)Math.Round(ParseNumber(MaxAttemptsText, "Attempts", "a whole number", parseErrors, Sidera.Runtime.Astrometry.RotationService.DefaultMaxAttempts)),
        ParseNumber(ExposureText, "Solve exposure", "a number of seconds", parseErrors, 5));
}

public sealed partial class CenterAndRotateStepDraftViewModel : StepDraftViewModel
{
    public CenterAndRotateStepDraftViewModel(Sidera.Runtime.Devices.DeviceRegistry registry, CenterAndRotateStepDraft draft, RigPickerViewModel rig) : base(draft.Id)
    {
        Mount = Picker(registry, IsMount, draft.MountId);
        Rig = rig;
        rig.Changed += (_, _) => NotifyEdited();
        RightAscensionText = Precise(draft.RightAscensionHours);
        DeclinationText = Precise(draft.DeclinationDegrees);
        ToleranceText = Format(draft.ToleranceArcseconds);
        MaxCenteringAttemptsText = Format(draft.MaxCenteringAttempts);
        AngleText = Format(draft.SkyRotationDegrees);
        RotationToleranceText = Format(draft.RotationToleranceDegrees);
        MaxRotationAttemptsText = Format(draft.MaxRotationAttempts);
        MaxRoundsText = Format(draft.MaxRounds);
        ExposureText = Format(draft.ExposureSeconds);
        _targetName = draft.TargetName;
    }

    private static string Precise(double value) => value.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    private readonly string? _targetName;

    /// <summary>The framing target the step came from, as one line; empty for a step that was made by hand.</summary>
    public string FramingText => _targetName is null ? string.Empty : "Framing: " + _targetName;

    public override SequenceStepKind Kind => SequenceStepKind.CenterAndRotate;
    public DevicePickerViewModel Mount { get; }
    public RigPickerViewModel Rig { get; }
    [ObservableProperty] public partial string RightAscensionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeclinationText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ToleranceText { get; set; } = "60";
    [ObservableProperty] public partial string MaxCenteringAttemptsText { get; set; } = "5";
    [ObservableProperty] public partial string AngleText { get; set; } = "0";
    [ObservableProperty] public partial string RotationToleranceText { get; set; } = "0.5";
    [ObservableProperty] public partial string MaxRotationAttemptsText { get; set; } = "4";
    [ObservableProperty] public partial string MaxRoundsText { get; set; } = "3";
    [ObservableProperty] public partial string ExposureText { get; set; } = "5";
    internal override IEnumerable<DevicePickerViewModel> Pickers => [Mount];
    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];
    internal override SequenceStepDraft Read(List<string> parseErrors) => new CenterAndRotateStepDraft(
        Id, Mount.SelectedId, Rig.SelectedId,
        ParseNumber(RightAscensionText, "Right ascension", "a number of hours", parseErrors, 0),
        ParseNumber(DeclinationText, "Declination", "a number of degrees", parseErrors, 0),
        ParseNumber(ToleranceText, "Tolerance", "a number of arcseconds", parseErrors, 60),
        (int)Math.Round(ParseNumber(MaxCenteringAttemptsText, "Centering attempts", "a whole number", parseErrors, 5)),
        ParseNumber(AngleText, "Sky rotation", "a number of degrees", parseErrors, 0),
        ParseNumber(RotationToleranceText, "Rotation tolerance", "a number of degrees", parseErrors, Sidera.Runtime.Astrometry.RotationService.DefaultToleranceDegrees),
        (int)Math.Round(ParseNumber(MaxRotationAttemptsText, "Rotation attempts", "a whole number", parseErrors, Sidera.Runtime.Astrometry.RotationService.DefaultMaxAttempts)),
        (int)Math.Round(ParseNumber(MaxRoundsText, "Rounds", "a whole number", parseErrors, Sidera.Runtime.Astrometry.RotationService.DefaultMaxRounds)),
        ParseNumber(ExposureText, "Solve exposure", "a number of seconds", parseErrors, 5), _targetName);
}
