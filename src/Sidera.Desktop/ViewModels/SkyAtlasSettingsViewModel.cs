using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Desktop.Settings;
using Sidera.Sky;

namespace Sidera.Desktop.ViewModels;

/// <summary>The settings of the sky atlas: the survey it opens with, the cache and the network timeout. Few on purpose.</summary>
public sealed partial class SkyAtlasSettingsViewModel : ViewModelBase
{
    private readonly SiteService _settings;

    public SkyAtlasSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        var s = settings.SkyAtlas;
        SelectedSurvey = Surveys.FirstOrDefault(x => x.Id == s.DefaultSurveyId) ?? Surveys[0];
        CacheDirectory = s.CacheDirectory ?? string.Empty;
        MaxCacheMegabytes = s.MaxCacheMegabytes;
        NetworkTimeoutSeconds = s.NetworkTimeoutSeconds;
    }

    public IReadOnlyList<SkySurveyDescriptor> Surveys { get; } = SkySurveys.Defaults;

    [ObservableProperty] public partial SkySurveyDescriptor SelectedSurvey { get; set; }
    [ObservableProperty] public partial string CacheDirectory { get; set; }
    [ObservableProperty] public partial int MaxCacheMegabytes { get; set; }
    [ObservableProperty] public partial double NetworkTimeoutSeconds { get; set; }
    [ObservableProperty] public partial string ProblemText { get; private set; } = string.Empty;

    [RelayCommand]
    private void Save()
    {
        var settings = new SkyAtlasSettings
        {
            DefaultSurveyId = SelectedSurvey.Id,
            CacheDirectory = string.IsNullOrWhiteSpace(CacheDirectory) ? null : CacheDirectory.Trim(),
            MaxCacheMegabytes = MaxCacheMegabytes,
            NetworkTimeoutSeconds = NetworkTimeoutSeconds,
        };
        ProblemText = _settings.SetSkyAtlas(settings).Problem ?? "Saved. A new cache folder or size counts from the next start.";
    }
}
