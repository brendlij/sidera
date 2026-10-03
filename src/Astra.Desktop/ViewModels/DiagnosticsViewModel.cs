using System;
using Astra.Desktop.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// Where the log is: the file of this session, the folder, the level and the session id, and a way to open the folder. No
/// log viewer, no filtering: the file is what goes into a bug report.
/// </summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly LogInfo? _info;
    private readonly IFolderOpener _opener;

    public DiagnosticsViewModel(LogInfo? info = null, IFolderOpener? opener = null)
    {
        _info = info;
        _opener = opener ?? new ShellFolderOpener();
    }

    public bool IsLoggingConfigured => _info is not null;

    public string LogFileText => _info?.CurrentFile ?? "Logging is not configured.";

    public string LogFolderText => _info?.Directory ?? string.Empty;

    public string LevelText => _info is { } i ? i.MinimumLevel.ToString() : string.Empty;

    public string SessionText => _info?.SessionId ?? string.Empty;

    public string VersionText => AstraLogging.Version();

    [RelayCommand(CanExecute = nameof(IsLoggingConfigured))]
    private void OpenLogFolder()
    {
        ClearError();
        try
        {
            _opener.Open(_info!.Directory);
        }
        catch (Exception ex)
        {
            // Nothing to open it with (no desktop): the path is shown, and can be copied from the page.
            ReportError($"The folder could not be opened. It is {_info!.Directory}", ex);
        }
    }
}
