using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Desktop.Settings;
using Sidera.Runtime.Location;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The site of a connected mount against the observing site of Sidera. When the mount has connected, the two are compared once
/// (<see cref="MountSiteSynchronizer"/> owns the policy); only a place that differs by more than the tolerance, or a site that exists on one side only,
/// is shown, and then as a choice that is never made by itself: use the mount's place as Sidera's, send Sidera's place to the mount (when the mount takes
/// one), or keep both as they are. "Keep both" only closes the card for this connection.
/// </summary>
public sealed partial class MountSiteViewModel : ViewModelBase, IDisposable
{
    private readonly MountViewModel _mount;
    private readonly IMountControl? _control;
    private readonly SiteService? _site;
    private bool _dismissed;

    public MountSiteViewModel(MountViewModel mount, SiteService? site)
    {
        _mount = mount;
        _control = mount.DeviceModel as IMountControl;
        _site = site;
        if (_site is not null)
        {
            _mount.PropertyChanged += OnMountChanged;
            _site.Changed += OnSiteChanged;
            Evaluate();
        }
    }

    /// <summary>The comparison that is shown; <c>null</c> when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMismatch), nameof(IsMountOffering), nameof(IsMountWithoutSite), nameof(IsVisible), nameof(CanSend), nameof(Headline), nameof(Explanation))]
    public partial SiteAssessment? Assessment { get; private set; }

    public bool IsVisible => Assessment is { } a && !_dismissed && a.Situation is SiteSituation.Mismatch or SiteSituation.MountOffersSite or SiteSituation.MountHasNoSite;

    public bool IsMismatch => IsVisible && Assessment!.Situation == SiteSituation.Mismatch;

    public bool IsMountOffering => IsVisible && Assessment!.Situation == SiteSituation.MountOffersSite;

    public bool IsMountWithoutSite => IsVisible && Assessment!.Situation == SiteSituation.MountHasNoSite;

    /// <summary>The mount takes a site from Sidera.</summary>
    public bool CanSend => IsVisible && Assessment is { CanSendToMount: true };

    public bool CanUseMount => IsVisible && Assessment?.Mount is not null;

    public string Headline => Assessment?.Situation switch
    {
        SiteSituation.Mismatch => "LOCATION MISMATCH",
        SiteSituation.MountOffersSite => "MOUNT LOCATION",
        SiteSituation.MountHasNoSite => "MOUNT HAS NO LOCATION",
        _ => string.Empty,
    };

    public string Explanation => Assessment?.Situation switch
    {
        SiteSituation.Mismatch => "The mount is using a different observing location.",
        SiteSituation.MountOffersSite => "Sidera has no observing site yet. The mount knows one.",
        SiteSituation.MountHasNoSite => "Sidera has an observing site; the mount does not report one.",
        _ => string.Empty,
    };

    public string SideraText => Describe(Assessment?.Sidera, "Not configured");

    public string MountText => Describe(Assessment?.Mount, "Not reported");

    /// <summary>How far apart the two places are, when both exist: "Differs by 412 km horizontally and 110 m in elevation".</summary>
    public string DifferenceText => Assessment?.Comparison is { } c
        ? FormattableString.Invariant($"Differs by {FormatDistance(c.HorizontalDistanceMeters)} horizontally and {c.ElevationDifferenceMeters:0} m in elevation.")
        : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; private set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>What the last choice did, once it succeeded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultText { get; private set; } = string.Empty;

    public bool HasResult => ResultText.Length > 0;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Compares the two sites again (after a connection, or when the site of Sidera changed).</summary>
    public void Evaluate()
    {
        if (_site is null || _control is null || _mount.ConnectionState != DeviceConnectionState.Connected)
        {
            Assessment = null;
            RaiseTexts();
            return;
        }

        var write = _control.Capabilities.Value?.SiteWrite ?? MountSiteWriteSupport.NotSupported;
        Assessment = MountSiteSynchronizer.Assess(_site.Site, _control.Site, write);
        RaiseTexts();
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(SideraText));
        OnPropertyChanged(nameof(MountText));
        OnPropertyChanged(nameof(DifferenceText));
        OnPropertyChanged(nameof(CanUseMount));
        UseMountLocationCommand.NotifyCanExecuteChanged();
        SendSideraLocationCommand.NotifyCanExecuteChanged();
    }

    private void OnMountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MountViewModel.ConnectionState))
        {
            return;
        }

        // A new connection is a new question; the choice of the last one is over.
        _dismissed = false;
        ProblemText = string.Empty;
        ResultText = string.Empty;
        Evaluate();
    }

    private void OnSiteChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>Makes the place of the mount the observing site of Sidera; the name of the site stays. The mount is not changed.</summary>
    [RelayCommand(CanExecute = nameof(CanUseMount))]
    private void UseMountLocation()
    {
        ProblemText = string.Empty;
        if (_site is null || _control?.Site is not { } mountSite)
        {
            return;
        }

        if (MountSiteSynchronizer.AdoptMountSite(mountSite, _site.Site) is not { } adopted)
        {
            ProblemText = "The mount does not report a valid location.";
            return;
        }

        var result = _site.Set(adopted);
        if (!result.Succeeded)
        {
            ProblemText = result.Problem ?? "The site could not be saved.";
            return;
        }

        ResultText = "Sidera now uses the location of the mount.";
        Evaluate();
    }

    /// <summary>Writes the observing site of Sidera to the mount and checks what the mount reports. Tried once.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendSideraLocationAsync()
    {
        ProblemText = string.Empty;
        if (_control is not IMountSiteControl writable || _site?.Site is not { } site)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await MountSiteSynchronizer.SendToMountAsync(writable, site);
            if (!outcome.Succeeded)
            {
                ProblemText = outcome.Problem ?? "The mount did not take the site.";
                Evaluate();
                return;
            }

            ResultText = "The mount now has the location of Sidera (read back from the mount).";
            Evaluate();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Closes the card for this connection. Nothing is changed, and nothing is remembered beyond it.</summary>
    [RelayCommand]
    private void KeepBoth()
    {
        _dismissed = true;
        ProblemText = string.Empty;
        ResultText = string.Empty;
        Evaluate();
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsMismatch));
        OnPropertyChanged(nameof(IsMountOffering));
        OnPropertyChanged(nameof(IsMountWithoutSite));
        OnPropertyChanged(nameof(CanSend));
    }

    private static string Describe(ObservingSite? site, string none) => site is null
        ? none
        : (site.Name is { } name ? name + "\n" : string.Empty)
          + $"{GeoCoordinateFormat.FormatLatitude(site.LatitudeDegrees)}\n{GeoCoordinateFormat.FormatLongitude(site.LongitudeDegrees)}\n{GeoCoordinateFormat.FormatElevation(site.ElevationMeters)}";

    private static string FormatDistance(double meters) =>
        meters >= 1000 ? FormattableString.Invariant($"{meters / 1000:0.#} km") : FormattableString.Invariant($"{meters:0} m");

    public void Dispose()
    {
        _mount.PropertyChanged -= OnMountChanged;
        if (_site is not null)
        {
            _site.Changed -= OnSiteChanged;
        }
    }
}
