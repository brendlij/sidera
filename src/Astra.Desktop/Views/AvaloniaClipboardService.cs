using System;
using System.Threading.Tasks;
using Astra.Desktop.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Astra.Desktop.Views;

/// <summary>The clipboard of the window Astra shows.</summary>
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
