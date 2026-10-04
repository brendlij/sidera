using System.Threading.Tasks;

namespace Sidera.Desktop.Documents;

/// <summary>
/// Lets the user choose a sequence file. The view models ask through this; how a dialog looks, or whether there is one,
/// is up to the platform (the desktop app implements it with Avalonia's storage provider). Only files on the local
/// disk are supported, so both answers are paths.
/// </summary>
public interface ISequenceFilePicker
{
    /// <summary>Asks for an existing sequence file; <c>null</c> if the user cancelled.</summary>
    Task<string?> PickOpenPathAsync();

    /// <summary>Asks where to save; <c>null</c> if the user cancelled. The path may still lack the Sidera extension.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName);
}
