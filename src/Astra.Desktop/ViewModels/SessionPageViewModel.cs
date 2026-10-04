namespace Astra.Desktop.ViewModels;

/// <summary>
/// The Session page: an imaging session as a workflow, with the inspector of the selected step next to it, the
/// equipment the session shares, the controls of the document (new, open, save) and, apart from them, those of the run.
/// The dashboard shows the same <see cref="SequencerViewModel"/> as live status.
/// </summary>
public sealed class SessionPageViewModel(
    SequenceDocumentViewModel document,
    SequenceDraftViewModel draft,
    SequencerViewModel sequencer,
    SharedEquipmentViewModel shared,
    ExecutionOverviewViewModel execution) : ViewModelBase
{
    public SequenceDocumentViewModel Document { get; } = document;

    /// <summary>The mount and the guider the whole session shares, with their state.</summary>
    public SharedEquipmentViewModel Shared { get; } = shared;
    public SequenceDraftViewModel Draft { get; } = draft;
    public SequencerViewModel Sequencer { get; } = sequencer;

    /// <summary>What the running sequence is doing: lanes for the tracks of a Multi-Rig block, otherwise the running step.</summary>
    public ExecutionOverviewViewModel Execution { get; } = execution;
}
