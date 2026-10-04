using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Astra.Desktop.Diagnostics;
using Astra.Runtime.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Astra.Desktop.ViewModels;

/// <summary>One recent warning or error, as the diagnostics page lists it.</summary>
public sealed record ProblemRow(string TimeText, string LevelText, StatusKind Kind, string Source, string Message);

/// <summary>
/// Where the log is and how the session has gone: its id, the level, the file and folder (with commands to open the
/// folder and copy the path), and how many warnings and errors there were, with the latest few. No log viewer and no
/// filtering: the file is what goes into a bug report.
/// </summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase, IDisposable
{
    private readonly LogInfo? _info;
    private readonly IFolderOpener _opener;
    private readonly IClipboardService _clipboard;
    private readonly Action<Action> _postToUi;

    public DiagnosticsViewModel(
        LogInfo? info = null, IFolderOpener? opener = null, IClipboardService? clipboard = null, Action<Action>? postToUi = null)
    {
        _info = info;
        _opener = opener ?? new ShellFolderOpener();
        _clipboard = clipboard ?? new NoClipboardService();
        _postToUi = postToUi ?? (action => action());

        if (info?.Summary is { } summary)
        {
            summary.Changed += OnSummaryChanged;
        }

        RefreshProblems();
    }

    public bool IsLoggingConfigured => _info is not null;

    public string LogFileText => _info?.CurrentFile ?? "Logging is not configured.";

    /// <summary>Only the name of the file, which is what a user quotes: <c>astra-2026-10-03-221530.log</c>.</summary>
    public string LogFileName => _info?.CurrentFile is { } file ? Path.GetFileName(file) : "Not configured";

    public string LogFolderText => _info?.Directory ?? string.Empty;

    public string LevelText => _info is { } i ? i.MinimumLevel.ToString() : string.Empty;

    public string SessionText => _info?.SessionId ?? string.Empty;

    public string VersionText => AstraLogging.DisplayVersion();

    // Warnings and errors

    /// <summary>The session has logged warnings or errors, and the log has them.</summary>
    public bool HasSummary => _info?.Summary is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemsText))]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial int WarningCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemsText))]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial int ErrorCount { get; private set; }

    /// <summary>The latest warnings and errors, newest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ProblemRow> RecentProblems { get; private set; } = [];

    public bool HasProblems => WarningCount + ErrorCount > 0;

    /// <summary>"No warnings or errors", or "2 warnings · 1 error".</summary>
    public string ProblemsText => !HasProblems
        ? "No warnings or errors"
        : string.Join(" · ", new[]
        {
            WarningCount > 0 ? Plural(WarningCount, "warning") : null,
            ErrorCount > 0 ? Plural(ErrorCount, "error") : null,
        }.OfType<string>());

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private void OnSummaryChanged(object? sender, EventArgs e) => _postToUi(RefreshProblems);

    private void RefreshProblems()
    {
        if (_info?.Summary is not { } summary)
        {
            return;
        }

        WarningCount = summary.WarningCount;
        ErrorCount = summary.ErrorCount;
        RecentProblems = summary.Recent.Reverse().Select(problem => new ProblemRow(
            problem.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            problem.Level == LogLevel.Warning ? "Warning" : "Error",
            problem.Level == LogLevel.Warning ? StatusKind.Warning : StatusKind.Error,
            problem.Category,
            problem.Message)).ToList();
    }

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

    /// <summary>Copies the path of the log file: what a bug report names.</summary>
    [RelayCommand(CanExecute = nameof(IsLoggingConfigured))]
    private async Task CopyPathAsync()
    {
        ClearError();
        try
        {
            await _clipboard.SetTextAsync(_info!.CurrentFile ?? _info.Directory);
        }
        catch (Exception ex)
        {
            ReportError("The path could not be copied.", ex);
        }
    }

    public void Dispose()
    {
        if (_info?.Summary is { } summary)
        {
            summary.Changed -= OnSummaryChanged;
        }
    }
}
