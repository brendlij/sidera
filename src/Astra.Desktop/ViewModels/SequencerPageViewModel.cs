namespace Astra.Desktop.ViewModels;

/// <summary>
/// The Sequencer page: the steps of the sequence with the parameters of the selected step next to them. The dashboard
/// shows the same <see cref="SequencerViewModel"/> as a read-only outline.
/// </summary>
public sealed class SequencerPageViewModel(
    SequenceDocumentViewModel document, SequenceDraftViewModel draft, SequencerViewModel sequencer) : ViewModelBase
{
    public SequenceDocumentViewModel Document { get; } = document;
    public SequenceDraftViewModel Draft { get; } = draft;
    public SequencerViewModel Sequencer { get; } = sequencer;
}
