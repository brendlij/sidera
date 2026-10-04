using System.Threading.Tasks;
using Sidera.Desktop.Documents;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sidera.Desktop.Views;

/// <summary>
/// Asks for sequence files with Avalonia's storage provider, so it is the same on every platform Avalonia runs on. It
/// needs the window the dialogs belong to; until <see cref="Attach"/> was called, nothing can be picked. Only files
/// on the local disk are returned.
/// </summary>
public sealed class AvaloniaSequenceFilePicker : ISequenceFilePicker
{
    private static readonly FilePickerFileType SequenceFiles = new(SequenceDocumentFiles.FilterName)
    {
        Patterns = [SequenceDocumentFiles.Pattern],
    };

    private TopLevel? _owner;

    public void Attach(TopLevel owner) => _owner = owner;

    public async Task<string?> PickOpenPathAsync()
    {
        if (_owner?.StorageProvider is not { CanOpen: true } provider)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Sequence",
            AllowMultiple = false,
            FileTypeFilter = [SequenceFiles],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        if (_owner?.StorageProvider is not { CanSave: true } provider)
        {
            return null;
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Sequence",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = SequenceDocumentFiles.Extension.TrimStart('.'),
            FileTypeChoices = [SequenceFiles],
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }
}
