namespace Astra.Desktop.ViewModels;

/// <summary>
/// The Sequencer page: the steps of the sequence with the parameters of the selected step next to them. The dashboard
/// shows the same <see cref="SequencerViewModel"/> as a read-only outline.
/// </summary>
public sealed class SequencerPageViewModel(
    SequenceDocumentViewModel document,
    SequenceDraftViewModel draft,
    SequencerViewModel sequencer,
    SharedEquipmentViewModel shared) : ViewModelBase
{
    public SequenceDocumentViewModel Document { get; } = document;

    /// <summary>The mount and the guider the whole session shares, with their state.</summary>
    public SharedEquipmentViewModel Shared { get; } = shared;
    public SequenceDraftViewModel Draft { get; } = draft;
    public SequencerViewModel Sequencer { get; } = sequencer;
}
