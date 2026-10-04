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
/// The settings page. The observing site is the one setting a user can change; for the rest the page does not pretend: it names the
/// groups the settings will live in (general, appearance, logging, equipment defaults) and shows what is decided today,
/// read-only. A setting is added here when it is real.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(LogInfo? log = null, Sidera.Desktop.Settings.SiteService? site = null)
    {
        Site = site is null ? null : new SiteSettingsViewModel(site);
        PlateSolving = site is null ? null : new PlateSolvingSettingsViewModel(site);
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

    /// <summary>The observing site; <c>null</c> without a settings store (tests of other pages).</summary>
    public SiteSettingsViewModel? Site { get; }

    public bool HasSite => Site is not null;
    public PlateSolvingSettingsViewModel? PlateSolving { get; }

    public IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>The sentence that says why nothing can be changed here yet.</summary>
    public string NoteText => "The observing site can be set here, along with plate solving. Application information and build defaults are listed below.";
}
