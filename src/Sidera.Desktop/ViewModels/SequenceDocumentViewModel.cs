using System;
using System.IO;
using System.Threading.Tasks;
using Sidera.Desktop.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The sequence being edited as a document: which file it came from or went to, whether it has changes that are not
/// saved, and New, Open, Save and Save As. It works with <see cref="SequenceDocument"/>s through an
/// <see cref="ISequenceDocumentStore"/> and asks for files through an <see cref="ISequenceFilePicker"/>; it does not
/// know how documents are encoded and never touches the running sequence, only the draft.
/// <para>
/// While a sequence runs (also pausing and paused) none of the four commands is available, for the same reason the
/// draft itself is locked. Opening is all or nothing: the file is read, checked and mapped completely before the draft
/// is replaced, and any problem leaves draft, file and unsaved state as they were.
/// </para>
/// <para>
/// New and Open on a draft with unsaved changes first ask "Discard unsaved changes?" in place, through
/// <see cref="IsConfirmingDiscard"/>, <see cref="ConfirmDiscardCommand"/> and <see cref="CancelDiscardCommand"/>.
/// </para>
/// </summary>
public sealed partial class SequenceDocumentViewModel : ViewModelBase
{
    public const string UntitledName = "Untitled Sequence";

    private enum PendingAction
    {
        New,
        Open
    }

    private readonly ISequenceDocumentStore _store;
    private readonly ISequenceFilePicker _picker;
    private PendingAction? _pending;
    private int _revision;

    public SequenceDocumentViewModel(SequenceDraftViewModel draft, ISequenceDocumentStore store, ISequenceFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(picker);
        Draft = draft;
        _store = store;
        _picker = picker;

        draft.Modified += OnDraftModified;
        draft.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SequenceDraftViewModel.IsEditable))
            {
                OnLockChanged();
            }
        };
    }

    public SequenceDraftViewModel Draft { get; }

    /// <summary>The file the sequence was opened from or last saved to; <c>null</c> if it has none yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    public partial string? FilePath { get; private set; }

    /// <summary>There are changes that are not in the file (or there is no file yet and the sequence was just created).</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>The file name, or "Untitled Sequence".</summary>
    public string DisplayName => FilePath is null ? UntitledName : Path.GetFileName(FilePath);

    public bool HasFile => FilePath is not null;

    /// <summary>A New or Open is waiting for the answer to "Discard unsaved changes?".</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDiscard { get; private set; }

    /// <summary>The document commands are available: no sequence is running.</summary>
    public bool CanChangeDocument => Draft.IsEditable;

    [RelayCommand(CanExecute = nameof(CanChangeDocument))]
    private Task NewAsync() => RequestAsync(PendingAction.New);

    [RelayCommand(CanExecute = nameof(CanChangeDocument))]
    private Task OpenAsync() => RequestAsync(PendingAction.Open);

    [RelayCommand(CanExecute = nameof(CanChangeDocument))]
    private Task SaveAsync() => FilePath is { } path ? WriteAsync(path) : SaveAsAsync();

    [RelayCommand(CanExecute = nameof(CanChangeDocument))]
    private async Task SaveAsAsync()
    {
        var picked = await _picker.PickSavePathAsync(FilePath is null ? SequenceDocumentFiles.DefaultFileName : Path.GetFileName(FilePath));
        if (picked is not null)
        {
            await WriteAsync(SequenceDocumentFiles.WithExtension(picked));
        }
    }

    [RelayCommand(CanExecute = nameof(IsConfirmingDiscard))]
    private async Task ConfirmDiscardAsync()
    {
        var action = _pending;
        _pending = null;
        IsConfirmingDiscard = false;
        if (action is { } pending && Draft.IsEditable)
        {
            await RunAsync(pending);
        }
    }

    [RelayCommand(CanExecute = nameof(IsConfirmingDiscard))]
    private void CancelDiscard()
    {
        _pending = null;
        IsConfirmingDiscard = false;
    }

    partial void OnIsConfirmingDiscardChanged(bool value)
    {
        ConfirmDiscardCommand.NotifyCanExecuteChanged();
        CancelDiscardCommand.NotifyCanExecuteChanged();
    }

    private async Task RequestAsync(PendingAction action)
    {
        if (IsDirty)
        {
            _pending = action;
            IsConfirmingDiscard = true;
            return;
        }

        await RunAsync(action);
    }

    private Task RunAsync(PendingAction action)
    {
        if (action == PendingAction.New)
        {
            StartNew();
            return Task.CompletedTask;
        }

        return OpenFileAsync();
    }

    // An empty sequence without a file. It counts as changed: it is not in any file.
    private void StartNew()
    {
        ClearError();
        Draft.Replace([], Draft.DefaultSharedEquipment);
        FilePath = null;
        IsDirty = true;
    }

    private async Task OpenFileAsync()
    {
        var path = await _picker.PickOpenPathAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            // Read, checked and mapped in full before anything of the current sequence is touched.
            var document = await _store.LoadAsync(path);
            var drafts = SequenceDocumentMapper.ToDrafts(document);
            var shared = SequenceDocumentMapper.ToSharedEquipment(document);

            if (!Draft.IsEditable)
            {
                ReportError("A sequence is running. Open it again when it has ended.");
                return;
            }

            Draft.Replace(drafts, shared);
            FilePath = path;
            IsDirty = false;
            ClearError();
        }
        catch (SequenceDocumentException ex)
        {
            ReportError(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError("Could not open sequence.", ex);
        }
    }

    private async Task WriteAsync(string path)
    {
        if (!Draft.IsEditable)
        {
            return;
        }

        if (Draft.HasUnreadableFields)
        {
            ReportError("Fix the marked values before saving.");
            return;
        }

        // Edits made while the file is being written are not in it, and must keep the document modified.
        var revision = _revision;
        try
        {
            var document = SequenceDocumentMapper.ToDocument(
                Draft.Snapshot(), Path.GetFileNameWithoutExtension(path), Draft.SharedEquipment);
            await _store.SaveAsync(path, document);
        }
        catch (SequenceDocumentException ex)
        {
            ReportError(ex.Message, ex);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError("Could not save sequence.", ex);
            return;
        }

        FilePath = path;
        if (revision == _revision)
        {
            IsDirty = false;
        }

        ClearError();
    }

    private void OnDraftModified(object? sender, EventArgs e)
    {
        _revision++;
        IsDirty = true;
    }

    private void OnLockChanged()
    {
        if (!Draft.IsEditable)
        {
            // A confirmation that was open when a run started is withdrawn.
            _pending = null;
            IsConfirmingDiscard = false;
        }

        OnPropertyChanged(nameof(CanChangeDocument));
        NewCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
    }
}
