using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Sidera.Desktop.Diagnostics;
using Sidera.Runtime.Diagnostics;

namespace Sidera.Desktop.ViewModels;

/// <summary>One line of a settings group: a name and the value it has.</summary>
public sealed record SettingRow(string Label, string Value);

/// <summary>A group of settings. <see cref="IsEditable"/> is false for what the build decides and the page only reports.</summary>
public sealed record SettingsGroup(string Title, string Description, IReadOnlyList<SettingRow> Rows, bool IsEditable = false)
{
    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// The settings page. Sidera has no setting a user can change yet, and the page does not pretend otherwise: it names the
/// groups the settings will live in (general, appearance, logging, equipment defaults) and shows what is decided today,
/// read-only. A setting is added here when it is real.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(LogInfo? log = null)
    {
        Groups =
        [
            new SettingsGroup(
                "General", "About this installation.",
                [
                    new SettingRow("Version", SideraLogging.DisplayVersion()),
                    new SettingRow("Platform", RuntimeInformation.OSDescription),
                    new SettingRow("Runtime", RuntimeInformation.FrameworkDescription),
                ]),
            new SettingsGroup(
                "Appearance", "How Sidera looks.",
                [new SettingRow("Theme", "Dark")]),
            new SettingsGroup(
                "Logging", "What Sidera writes to its log, and for how long it keeps it.",
                log is null
                    ? []
                    :
                    [
                        new SettingRow("Level", log.MinimumLevel.ToString()),
                        new SettingRow("Kept for", string.Create(CultureInfo.InvariantCulture, $"{LoggingOptions.DefaultRetentionDays} days")),
                        new SettingRow("Folder", log.Directory),
                    ]),
            new SettingsGroup(
                "Equipment defaults", "Defaults for new equipment. Each device will also have settings of its own, on its page under Equipment.",
                []),
        ];
    }

    public IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>The sentence that says why nothing can be changed here yet.</summary>
    public string NoteText => "Nothing can be changed here yet. Today these values are decided by the build; they are listed so that you know where each kind of setting will be.";
}
