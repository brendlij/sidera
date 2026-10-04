using System;
using System.Diagnostics;
using System.IO;

namespace Sidera.Desktop.Diagnostics;

/// <summary>Shows a folder in the file manager of the platform.</summary>
public interface IFolderOpener
{
    /// <exception cref="InvalidOperationException">The folder could not be opened.</exception>
    void Open(string folder);
}

/// <summary>
/// Hands the folder to the shell, which opens it with the program the platform has for folders (Explorer, Finder, the
/// desktop's file manager): <see cref="ProcessStartInfo.UseShellExecute"/> does that on Windows, macOS and Linux alike, so
/// no program is named here.
/// </summary>
public sealed class ShellFolderOpener : IFolderOpener
{
    public void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new InvalidOperationException($"The folder could not be opened: {ex.Message}", ex);
        }
    }
}
