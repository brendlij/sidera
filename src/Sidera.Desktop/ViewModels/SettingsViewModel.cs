using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
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

/// <summary>One tab of the settings page; exactly one is shown.</summary>
public sealed partial class SettingsTabViewModel(string key, string title, System.Action<string> select) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            select(Key);
        }
    }
}

/// <summary>
/// The settings page. The observing site is the one setting a user can change; for the rest the page does not pretend: it names the
/// groups the settings will live in (general, appearance, logging, equipment defaults) and shows what is decided today,
/// read-only. A setting is added here when it is real.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public const string GeneralTab = "general";
    public const string AppearanceTab = "appearance";
    public const string ObservatoryTab = "observatory";
    public const string PlateSolvingTab = "platesolving";
    public const string FramingTab = "framing";
    public const string ImagingTab = "imaging";
    public const string AutofocusTab = "autofocus";
    public const string GuidingTab = "guiding";
    public const string MeridianFlipTab = "meridianflip";
    public const string SequencerTab = "sequencer";
    public const string AdvancedTab = "advanced";

    public SettingsViewModel(
        LogInfo? log = null, Sidera.Desktop.Settings.SiteService? site = null, HardwareSafetyViewModel? safety = null, Sidera.Desktop.Themes.IThemeApplier? themes = null)
    {
        if (site is not null)
        {
            Appearance = new AppearanceSettingsViewModel(site, themes);
            Imaging = new ImagingSettingsViewModel(site);
            Autofocus = new AutofocusSettingsViewModel(site);
            Guiding = new GuidingSettingsViewModel(site);
            MeridianFlip = new MeridianFlipSettingsViewModel(site);
            Sequencer = new SequencerSettingsViewModel(site);
            Advanced = new AdvancedSettingsViewModel(site, log, safety);
        }

        Site = site is null ? null : new SiteSettingsViewModel(site);
        PlateSolving = site is null ? null : new PlateSolvingSettingsViewModel(site);
        SkyAtlas = site is null ? null : new SkyAtlasSettingsViewModel(site);
        // A tab exists only when it has something real to show: the site and the atlas need the settings store; the other groups (imaging, autofocus, guiding) have no
        // application-wide settings yet, because those live with their device or their step. Every editable tab saves with its own Save button (nothing is applied while typing),
        // so what is on screen and what is stored never differ without the user having pressed Save.
        List<SettingsTabViewModel> tabs = [new(GeneralTab, "General", SelectTab)];
        if (site is not null)
        {
            tabs.AddRange(
            [
                new(AppearanceTab, "Appearance", SelectTab), new(ObservatoryTab, "Observatory", SelectTab), new(ImagingTab, "Imaging", SelectTab), new(AutofocusTab, "Autofocus", SelectTab), new(GuidingTab, "Guiding", SelectTab),
                new(PlateSolvingTab, "Plate solving", SelectTab), new(FramingTab, "Framing", SelectTab), new(MeridianFlipTab, "Meridian Flip", SelectTab),
                new(SequencerTab, "Sequencer", SelectTab), new(AdvancedTab, "Advanced", SelectTab),
            ]);
        }

        Tabs = tabs;
        SelectTab(GeneralTab);
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
    public SkyAtlasSettingsViewModel? SkyAtlas { get; }
    public ImagingSettingsViewModel? Imaging { get; }
    public AutofocusSettingsViewModel? Autofocus { get; }
    public GuidingSettingsViewModel? Guiding { get; }
    public MeridianFlipSettingsViewModel? MeridianFlip { get; }
    public SequencerSettingsViewModel? Sequencer { get; }
    public AppearanceSettingsViewModel? Appearance { get; }
    public AdvancedSettingsViewModel? Advanced { get; }

    public IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>The tabs of the page, in order.</summary>
    public IReadOnlyList<SettingsTabViewModel> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsAppearance), nameof(IsObservatory), nameof(IsPlateSolving), nameof(IsFraming), nameof(IsImaging), nameof(IsAutofocus), nameof(IsGuiding), nameof(IsMeridianFlip), nameof(IsSequencer), nameof(IsAdvanced))]
    public partial string SelectedTabKey { get; private set; } = GeneralTab;

    public bool IsGeneral => SelectedTabKey == GeneralTab;
    public bool IsAppearance => SelectedTabKey == AppearanceTab;
    public bool IsObservatory => SelectedTabKey == ObservatoryTab;
    public bool IsPlateSolving => SelectedTabKey == PlateSolvingTab;
    public bool IsFraming => SelectedTabKey == FramingTab;
    public bool IsImaging => SelectedTabKey == ImagingTab;
    public bool IsAutofocus => SelectedTabKey == AutofocusTab;
    public bool IsGuiding => SelectedTabKey == GuidingTab;
    public bool IsMeridianFlip => SelectedTabKey == MeridianFlipTab;
    public bool IsSequencer => SelectedTabKey == SequencerTab;
    public bool IsAdvanced => SelectedTabKey == AdvancedTab;

    /// <summary>Shows a tab; a tab that does not exist is ignored.</summary>
    public void SelectTab(string key)
    {
        if (Tabs.All(t => t.Key != key))
        {
            return;
        }

        SelectedTabKey = key;
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Key == key;
        }
    }

    /// <summary>How the settings are saved, said on the page.</summary>
    public string SaveNoteText => "Each tab is saved with its own Save button; nothing is applied while you type.";

    /// <summary>The sentence that says why nothing can be changed here yet.</summary>
    public string NoteText => "Defaults for what is created from now on live here; what already exists (a session with its own settings) is not changed by them. Application information is on the General tab.";
}
