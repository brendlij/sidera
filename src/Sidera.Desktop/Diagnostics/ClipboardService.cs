using System.Threading.Tasks;

namespace Sidera.Desktop.Diagnostics;

/// <summary>Puts text on the clipboard of the system. The window provides it; view models only ask.</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>For where there is no window: nothing is copied.</summary>
public sealed class NoClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) => Task.CompletedTask;
}
