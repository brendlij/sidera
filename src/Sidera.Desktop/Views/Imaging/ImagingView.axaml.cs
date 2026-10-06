using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Views.Imaging;

public partial class ImagingView : UserControl
{
    public ImagingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImagingViewModel imaging)
            {
                imaging.PickSavePath = PickSavePathAsync;
            }
        };
    }

    // Where to save: the file dialog of the window; null when the person cancels.
    private async Task<string?> PickSavePathAsync(string suggestedName, string kind)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var isFits = kind == "fits";
        var folder = DataContext is ImagingViewModel { SaveDirectory: { } directory } && directory() is { Length: > 0 } path && System.IO.Directory.Exists(path)
            ? await storage.TryGetFolderFromPathAsync(path)
            : null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedStartLocation = folder,
            Title = isFits ? "Save the frame as FITS" : "Save the picture as PNG",
            SuggestedFileName = suggestedName,
            DefaultExtension = kind,
            FileTypeChoices =
            [
                new FilePickerFileType(isFits ? "FITS image" : "PNG picture") { Patterns = [isFits ? "*.fits" : "*.png"] },
            ],
        });
        return file?.TryGetLocalPath();
    }
}
