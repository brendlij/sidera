using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Location;
using Sidera.Desktop.Settings;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The observing site on the settings page: a name that is optional, the latitude, the longitude and the elevation. The fields take what
/// people write ("47.7192° N", "122.4194 W", "-33.8688", "410", "410 m") and show what is stored in the same words; the stored values are signed
/// degrees (north and east positive) whatever is typed. A value that is not valid is refused with a sentence and nothing is saved.
/// </summary>
public sealed partial class SiteSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SiteService _service;

    public SiteSettingsViewModel(SiteService service)
    {
        _service = service;
        _service.Changed += OnSiteChanged;
        Load();
        if (_service.Problem is { } problem)
        {
            ProblemText = problem;
        }
    }

    [ObservableProperty]
    public partial string NameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatitudeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LongitudeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ElevationText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; private set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>What is configured now, in one line, or that nothing is.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = NotConfigured;

    [ObservableProperty]
    public partial bool IsConfigured { get; private set; }

    public const string NotConfigured = "No site configured. Calculations that need a location stay unavailable.";

    private void Load()
    {
        var site = _service.Site;
        NameText = site?.Name ?? string.Empty;
        LatitudeText = site is null ? string.Empty : GeoCoordinateFormat.FormatLatitude(site.LatitudeDegrees);
        LongitudeText = site is null ? string.Empty : GeoCoordinateFormat.FormatLongitude(site.LongitudeDegrees);
        ElevationText = site is null ? string.Empty : GeoCoordinateFormat.FormatElevation(site.ElevationMeters);
        IsConfigured = site is not null;
        StatusText = site is null
            ? NotConfigured
            : $"{(site.Name is { } name ? name + ": " : string.Empty)}{GeoCoordinateFormat.FormatLatitude(site.LatitudeDegrees)}, {GeoCoordinateFormat.FormatLongitude(site.LongitudeDegrees)}, {GeoCoordinateFormat.FormatElevation(site.ElevationMeters)}";
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void OnSiteChanged(object? sender, EventArgs e) => Load();

    /// <summary>Saves the site from the fields; the first value that is not valid is named and nothing is saved.</summary>
    [RelayCommand]
    private void Save()
    {
        ProblemText = string.Empty;
        if (!GeoCoordinateFormat.TryParseLatitude(LatitudeText, out var latitude, out var problem)
            || !GeoCoordinateFormat.TryParseLongitude(LongitudeText, out var longitude, out problem)
            || !GeoCoordinateFormat.TryParseElevation(ElevationText, out var elevation, out problem))
        {
            ProblemText = problem ?? "The site is not valid.";
            return;
        }

        if (!ObservingSite.TryCreate(latitude, longitude, elevation, NameText, out var site, out problem))
        {
            ProblemText = problem ?? "The site is not valid.";
            return;
        }

        var result = _service.Set(site!);
        if (!result.Succeeded)
        {
            ProblemText = result.Problem ?? "The site could not be saved.";
        }
    }

    /// <summary>Forgets the site: the location is unknown again.</summary>
    [RelayCommand(CanExecute = nameof(IsConfigured))]
    private void Clear()
    {
        ProblemText = string.Empty;
        var result = _service.Clear();
        if (!result.Succeeded)
        {
            ProblemText = result.Problem ?? "The site could not be cleared.";
        }
    }

    partial void OnIsConfiguredChanged(bool value) => ClearCommand.NotifyCanExecuteChanged();

    public void Dispose() => _service.Changed -= OnSiteChanged;
}
