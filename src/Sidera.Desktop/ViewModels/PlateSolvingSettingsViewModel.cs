using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Astap;
using Sidera.Desktop.Settings;

namespace Sidera.Desktop.ViewModels;

public sealed partial class PlateSolvingSettingsViewModel : ViewModelBase
{
    private readonly SiteService _settings;
    public PlateSolvingSettingsViewModel(SiteService settings)
    {
        _settings = settings;
        var s = settings.PlateSolving;
        ExecutablePath = s.ExecutablePath ?? ""; DatabasePath = s.DatabasePath ?? "";
        TimeoutSeconds = s.TimeoutSeconds; SearchRadiusDegrees = s.SearchRadiusDegrees;
        Downsample = s.DownsampleFactor == 0 ? "Auto" : $"{s.DownsampleFactor}x";
        BlindFallback = s.BlindFallback; ExposureSeconds = s.ExposureSeconds;
        ToleranceArcseconds = s.CenteringToleranceArcseconds; MaxAttempts = s.MaxCenteringAttempts;
        RotationToleranceDegrees = s.RotationToleranceDegrees; MaxRotationAttempts = s.MaxRotationAttempts;
        Refresh();
    }
    public IReadOnlyList<string> Backends { get; } = ["ASTAP"];
    public IReadOnlyList<string> Downsamples { get; } = ["Auto", "1x", "2x", "4x"];
    [ObservableProperty] public partial string Backend { get; set; } = "ASTAP";
    [ObservableProperty] public partial string ExecutablePath { get; set; }
    [ObservableProperty] public partial string DatabasePath { get; set; }
    [ObservableProperty] public partial double TimeoutSeconds { get; set; }
    [ObservableProperty] public partial double SearchRadiusDegrees { get; set; }
    [ObservableProperty] public partial string Downsample { get; set; }
    [ObservableProperty] public partial bool BlindFallback { get; set; }
    [ObservableProperty] public partial double ExposureSeconds { get; set; }
    [ObservableProperty] public partial double ToleranceArcseconds { get; set; }
    [ObservableProperty] public partial int MaxAttempts { get; set; }
    [ObservableProperty] public partial double RotationToleranceDegrees { get; set; }
    [ObservableProperty] public partial int MaxRotationAttempts { get; set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial string ProblemText { get; private set; } = "";
    public Func<Task<string?>>? BrowseExecutable { get; set; }
    [RelayCommand] private async Task BrowseAsync()
    {
        if (BrowseExecutable is not null && await BrowseExecutable() is { } path) { ExecutablePath = path; Refresh(); }
    }
    [RelayCommand] private void Refresh()
    {
        var status = AstapLocator.Locate(new AstapConfiguration { ExecutablePath = ExecutablePath, DatabasePath = DatabasePath }, new SystemAstapFileSystem()).ToStatus();
        StatusText = string.Join(Environment.NewLine, status.Lines);
    }
    [RelayCommand] private void Save()
    {
        var s = new PlateSolvingSettings
        {
            Backend = Backend, ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
            DatabasePath = string.IsNullOrWhiteSpace(DatabasePath) ? null : DatabasePath.Trim(), TimeoutSeconds = TimeoutSeconds,
            SearchRadiusDegrees = SearchRadiusDegrees, DownsampleFactor = Downsample == "Auto" ? 0 : int.Parse(Downsample[..1]),
            BlindFallback = BlindFallback, ExposureSeconds = ExposureSeconds, CenteringToleranceArcseconds = ToleranceArcseconds, MaxCenteringAttempts = MaxAttempts,
            RotationToleranceDegrees = RotationToleranceDegrees, MaxRotationAttempts = MaxRotationAttempts
        };
        var result = _settings.SetPlateSolving(s);
        ProblemText = result.Problem ?? "Settings saved.";
        Refresh();
    }
}
