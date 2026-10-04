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
