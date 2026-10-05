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
        RightAscensionText = Format(draft.RightAscensionHours);
        DeclinationText = Format(draft.DeclinationDegrees);
        ToleranceText = Format(draft.ToleranceArcseconds);
        MaxAttemptsText = Format(draft.MaxAttempts);
        ExposureText = Format(draft.ExposureSeconds);
    }

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
        ParseNumber(ExposureText, "Solve exposure", "a number of seconds", parseErrors, 5));
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
