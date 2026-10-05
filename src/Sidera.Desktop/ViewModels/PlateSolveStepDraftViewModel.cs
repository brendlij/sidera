using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
namespace Sidera.Desktop.ViewModels;
public sealed partial class PlateSolveStepDraftViewModel : StepDraftViewModel
{
    public PlateSolveStepDraftViewModel(PlateSolveStepDraft draft, RigPickerViewModel rig) : base(draft.Id)
    {
        Rig = rig; rig.Changed += (_, _) => NotifyEdited(); ExposureText = Format(draft.ExposureSeconds);
    }
    public override SequenceStepKind Kind => SequenceStepKind.PlateSolve;
    public RigPickerViewModel Rig { get; }
    [ObservableProperty] public partial string ExposureText { get; set; } = "5";
    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];
    internal override SequenceStepDraft Read(List<string> parseErrors) => new PlateSolveStepDraft(Id, Rig.SelectedId,
        ParseNumber(ExposureText, "Solve exposure", "a number of seconds", parseErrors, 5));
}

public sealed partial class SlewAndCenterStepDraftViewModel : StepDraftViewModel
{
    public SlewAndCenterStepDraftViewModel(Sidera.Runtime.Devices.DeviceRegistry registry, SlewAndCenterStepDraft draft, RigPickerViewModel rig) : base(draft.Id)
    {
        Mount = Picker(registry, IsMount, draft.MountId);
        Rig = rig;
        rig.Changed += (_, _) => NotifyEdited();
        RightAscensionText = Precise(draft.RightAscensionHours);
        DeclinationText = Precise(draft.DeclinationDegrees);
        ToleranceText = Format(draft.ToleranceArcseconds);
        MaxAttemptsText = Format(draft.MaxAttempts);
        ExposureText = Format(draft.ExposureSeconds);
        _targetName = draft.TargetName;
        _desiredRotation = draft.DesiredRotationDegrees;
    }

    // A framing puts a position here: it is kept to the precision that a mount can use (about a tenth of an arcsecond), not rounded to a few decimals.
    private static string Precise(double value) => value.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    private readonly string? _targetName;
    private readonly double? _desiredRotation;

    /// <summary>The framing target the step came from, as one line; empty for a step that was made by hand.</summary>
    public string FramingText => _targetName is null && _desiredRotation is null ? string.Empty
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Framing: {_targetName ?? "target"}{(_desiredRotation is { } r ? $" · desired rotation {r:0.#}°" : string.Empty)}");

    public override SequenceStepKind Kind => SequenceStepKind.SlewAndCenter;
    public DevicePickerViewModel Mount { get; }
    public RigPickerViewModel Rig { get; }
    [ObservableProperty] public partial string RightAscensionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeclinationText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ToleranceText { get; set; } = "60";
    [ObservableProperty] public partial string MaxAttemptsText { get; set; } = "5";
    [ObservableProperty] public partial string ExposureText { get; set; } = "5";
    internal override IEnumerable<DevicePickerViewModel> Pickers => [Mount];
    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];
    internal override SequenceStepDraft Read(List<string> parseErrors) => new SlewAndCenterStepDraft(
        Id, Mount.SelectedId, Rig.SelectedId,
        ParseNumber(RightAscensionText, "Right ascension", "a number of hours", parseErrors, 0),
        ParseNumber(DeclinationText, "Declination", "a number of degrees", parseErrors, 0),
        ParseNumber(ToleranceText, "Tolerance", "a number of arcseconds", parseErrors, 60),
        (int)Math.Round(ParseNumber(MaxAttemptsText, "Attempts", "a whole number", parseErrors, 5)),
        ParseNumber(ExposureText, "Solve exposure", "a number of seconds", parseErrors, 5), _targetName, _desiredRotation);
}

public sealed class SyncMountStepDraftViewModel : StepDraftViewModel
{
    public SyncMountStepDraftViewModel(Sidera.Runtime.Devices.DeviceRegistry registry, SyncMountStepDraft draft) : base(draft.Id)
    {
        Mount = Picker(registry, IsMount, draft.MountId);
    }

    public override SequenceStepKind Kind => SequenceStepKind.SyncMountToSolved;
    public DevicePickerViewModel Mount { get; }
    internal override IEnumerable<DevicePickerViewModel> Pickers => [Mount];
    internal override SequenceStepDraft Read(List<string> parseErrors) => new SyncMountStepDraft(Id, Mount.SelectedId);
}
