using System;
using System.Threading.Tasks;
using Sidera.Desktop.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Sidera.Desktop.Views;

/// <summary>The clipboard of the window Sidera shows.</summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    private TopLevel? _window;

    public void Attach(TopLevel window) => _window = window;

    public async Task SetTextAsync(string text)
    {
        if (_window?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
