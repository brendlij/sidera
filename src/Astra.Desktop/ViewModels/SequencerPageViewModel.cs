namespace Astra.Desktop.ViewModels;

/// <summary>
/// The Sequencer page: the editable parameters next to the workflow. The dashboard shows the same
/// <see cref="SequencerViewModel"/> without the editor.
/// </summary>
public sealed class SequencerPageViewModel(SequenceSetupViewModel setup, SequencerViewModel sequencer) : ViewModelBase
{
    public SequenceSetupViewModel Setup { get; } = setup;
    public SequencerViewModel Sequencer { get; } = sequencer;
}
