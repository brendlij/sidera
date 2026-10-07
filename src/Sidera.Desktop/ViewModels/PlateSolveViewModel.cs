using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Settings;
using Sidera.Runtime;
using Sidera.Runtime.Astrometry;

namespace Sidera.Desktop.ViewModels;

public sealed partial class PlateSolveViewModel : ViewModelBase, IDisposable
{
    private readonly SideraRuntimeHost _host;
    private readonly ImagingViewModel _imaging;
    private readonly SiteService? _settings;
    private readonly ImagingSetupContext _context;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _cancel;
    public PlateSolveViewModel(SideraRuntimeHost host, ImagingViewModel imaging, ImagingSetupContext context, SiteService? settings, Action<Action> post)
    {
        _host = host; _imaging = imaging; _context = context; _settings = settings; _post = post;
        _context.Changed += (_, _) => RefreshEquipment();
        var s = settings?.PlateSolving ?? new(); ExposureSeconds = s.ExposureSeconds;
        ToleranceArcseconds = s.CenteringToleranceArcseconds; MaxAttempts = s.MaxCenteringAttempts;
        RefreshEquipment();
    }
    /// <summary>The mounts there are. The one of the current imaging setup is the mount; only when the setup has none and there are several is one chosen here.</summary>
    public ObservableCollection<IMount> Mounts { get; } = [];

    /// <summary>The current imaging setup: its camera captures, its optics give the hints. Nothing is chosen on this page.</summary>
    public Rig? Setup => _context.Current;

    public bool HasSetup => Setup is not null;

    /// <summary>The setup has no mount and there are several: which one is meant is asked, not guessed.</summary>
    public bool HasMountChoice => Setup is { MountId: null } && Mounts.Count > 1;

    [ObservableProperty] public partial IMount? SelectedMount { get; set; }
    [ObservableProperty] public partial double ExposureSeconds { get; set; }
    // The target of Slew & Center is never 0 h / 0 deg by default: it starts empty, or as the position of the mount when that is connected and the person has not typed anything. A target that
    // is typed is kept; a target that is not valid or not there disables the command and says why.
    private bool _targetEdited;
    private bool _fillingTarget;

    [ObservableProperty] public partial string RaText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DecText { get; set; } = string.Empty;

    partial void OnRaTextChanged(string value) => TargetTyped();
    partial void OnDecTextChanged(string value) => TargetTyped();

    private void TargetTyped()
    {
        if (!_fillingTarget)
        {
            _targetEdited = true;
        }

        OnPropertyChanged(nameof(CenterDisabledText));
        SlewAndCenterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The target as entered; <c>null</c> when it is empty or not a position.</summary>
    public CelestialCoordinates? Target
    {
        get
        {
            if (!TryNumber(RaText, out var ra) || !TryNumber(DecText, out var dec) || ra < 0 || ra >= 24 || dec is < -90 or > 90)
            {
                return null;
            }

            return new CelestialCoordinates(ra, dec);
        }
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Why Slew &amp; Center cannot be used now, in a sentence; empty when it can.</summary>
    public string CenterDisabledText =>
        _host.PlateSolving is null ? "No plate solver is configured."
        : Setup is null ? _context.NoSetupText
        : SelectedMount is null ? "No mount is available."
        : SelectedMount.ConnectionState != DeviceConnectionState.Connected ? "Connect the mount to slew."
        : RaText.Trim().Length == 0 || DecText.Trim().Length == 0 ? "Enter the target (RA in hours, Dec in degrees); nothing is assumed."
        : Target is null ? "The target is not a position: RA is 0 to 24 h, Dec is -90 to 90 degrees."
        : string.Empty;

    // The target the mount is at, for a target that nobody has typed.
    private void FillTargetFromMount()
    {
        if (_targetEdited || SelectedMount is not { ConnectionState: DeviceConnectionState.Connected } mount)
        {
            return;
        }

        try
        {
            var at = mount.Coordinates;
            _fillingTarget = true;
            RaText = at.RightAscensionHours.ToString("0.#####", CultureInfo.InvariantCulture);
            DecText = at.DeclinationDegrees.ToString("0.#####", CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            // A mount that does not say where it points: the target stays empty.
        }
        finally
        {
            _fillingTarget = false;
        }
    }
    [ObservableProperty] public partial double ToleranceArcseconds { get; set; }
    [ObservableProperty] public partial int MaxAttempts { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    partial void OnIsBusyChanged(bool value) => SlewAndCenterCommand.NotifyCanExecuteChanged();
    [ObservableProperty] public partial string StatusText { get; private set; } = "Ready";
    [ObservableProperty] public partial string ResultText { get; private set; } = "No solve yet.";
    [ObservableProperty] public partial string HintText { get; private set; } = string.Empty;
    public string SolverName => _host.PlateSolving?.Solver.Name ?? "No solver configured";
    public string CameraName => Setup is { } r && _host.DeviceRegistry.TryGet(r.CameraId, out var camera) ? camera!.Name : "Unknown";

    /// <summary>The setup the page works with, as a name; empty when there is none.</summary>
    public string SetupName => _context.Name;
    [RelayCommand] public void RefreshEquipment()
    {
        var mountId = SelectedMount?.Id;
        Mounts.Clear(); foreach (var m in _host.DeviceRegistry.GetAll().OfType<IMount>()) Mounts.Add(m);

        // The mount: the one of the setup; else the one that was chosen; else the only one. Never the first of several.
        SelectedMount = Setup?.MountId is { } setupMount ? Mounts.FirstOrDefault(m => m.Id == setupMount)
            : Mounts.FirstOrDefault(m => m.Id == mountId) ?? (Mounts.Count == 1 ? Mounts[0] : null);
        OnPropertyChanged(nameof(Setup));
        OnPropertyChanged(nameof(HasSetup));
        OnPropertyChanged(nameof(SetupName));
        OnPropertyChanged(nameof(CameraName));
        OnPropertyChanged(nameof(HasMountChoice));
        RefreshHints();
    }
    partial void OnSelectedMountChanged(IMount? value) => RefreshHints();
    private PlateSolveDefaults Defaults => (_settings?.PlateSolving ?? new()).Defaults();
    private void RefreshHints()
    {
        if (Setup is not { } rig)
        {
            HintText = _context.NoSetupText;
            OnPropertyChanged(nameof(CenterDisabledText));
            SlewAndCenterCommand.NotifyCanExecuteChanged();
            return;
        }

        _host.DeviceRegistry.TryGet(rig.CameraId, out var camera);
        var geometry = OpticalTrainGeometry.Resolve(rig.Optics, SensorGeometry.For(camera));
        CelestialCoordinates? center = null;
        try { if (SelectedMount?.ConnectionState == DeviceConnectionState.Connected) center = SelectedMount.Coordinates; } catch { }
        FillTargetFromMount();
        OnPropertyChanged(nameof(CenterDisabledText));
        SlewAndCenterCommand.NotifyCanExecuteChanged();
        HintText = $"Approximate RA/Dec: {(center is null ? "Unknown" : FormattableString.Invariant($"{center.RightAscensionHours:0.#####} h / {center.DeclinationDegrees:0.#####}°"))}\n" +
            FormattableString.Invariant($"Scale: {geometry.PixelScaleXArcsecPerPixel:0.###} × {geometry.PixelScaleYArcsecPerPixel:0.###} arcsec/px · FOV: {geometry.FieldOfViewXDegrees:0.###} × {geometry.FieldOfViewYDegrees:0.###}° · Focal length: {geometry.FocalLengthMm:0.##} mm");
    }
    [RelayCommand] private Task CaptureAndSolveAsync() => RunAsync(async (service, rig, token) =>
    {
        var result = await service.CaptureAndSolveAsync(rig, SelectedMount?.Id, TimeSpan.FromSeconds(ExposureSeconds), Defaults, cancellationToken: token);
        if (service.LastFrame is { } frame) _imaging.Publish(frame, $"{rig.Name} (plate solve)", rig.CameraId);
        ShowResult(service, result);
    });
    [RelayCommand] private Task SolveLastFrameAsync() => RunAsync(async (service, rig, token) =>
    {
        var frame = _imaging.LatestFrame ?? throw new InvalidOperationException("Capture a frame first.");
        if (_imaging.LatestCameraId is { } cameraId && cameraId != rig.CameraId)
            throw new InvalidOperationException("The last frame was taken with another camera. Choose the imaging setup of that camera, or capture again.");
        ShowResult(service, await service.SolveAsync(frame, rig, SelectedMount?.Id, Defaults, cancellationToken: token));
    });
    private bool CanSlewAndCenter() => !IsBusy && CenterDisabledText.Length == 0;

    /// <summary>What asks before the mount moves; <c>null</c> where nothing is asked. Set by the main view model.</summary>
    public HardwareSafetyViewModel? Safety { get; set; }

    [RelayCommand(CanExecute = nameof(CanSlewAndCenter))] private async Task SlewAndCenterAsync()
    {
        // A real mount asks first: no means nothing moves.
        if (Safety is not null && SelectedMount is { } chosen
            && !await Safety.ConfirmAsync("Slew & Center", [HardwareSafetyViewModel.NoticeFor(_host.DeviceRegistry, MovingEquipment.Mount, chosen.Id)]))
        {
            StatusText = "Cancelled: nothing was moved.";
            return;
        }

        await SlewAndCenterCoreAsync();
    }

    private Task SlewAndCenterCoreAsync() => RunAsync(async (service, rig, token) =>
    {
        var mount = SelectedMount ?? throw new InvalidOperationException("Choose a mount.");
        var target = Target ?? throw new InvalidOperationException(CenterDisabledText);
        var progress = new Progress<CenteringProgress>(p => _post(() => StatusText = FormattableString.Invariant($"{p.Stage} · attempt {p.Attempt} · error {p.PointingErrorArcseconds:0.##} arcsec")));
        var result = await service.CenterTargetAsync(target, rig, mount.Id, ToleranceArcseconds, MaxAttempts,
            TimeSpan.FromSeconds(ExposureSeconds), Defaults, progress: progress, cancellationToken: token);
        if (service.LastResult is { } solve) ShowResult(service, solve);
        StatusText = result.Success ? "Centered" : result.Message ?? "Centering failed.";
    });
    /// <summary>Explicit only: tells the mount where the last solve found it. Never done by a solve, a sequence or centering.</summary>
    [RelayCommand] private Task SyncMountAsync() => RunAsync(async (service, rig, token) =>
    {
        var mount = SelectedMount ?? throw new InvalidOperationException("Choose a mount.");
        await service.SyncMountToSolvedPositionAsync(mount.Id, token);
        StatusText = "Mount synchronized to the solved position.";
    });
    [RelayCommand] private void Cancel() => _cancel?.Cancel();
    private async Task RunAsync(Func<PlateSolveService, Rig, CancellationToken, Task> body)
    {
        if (IsBusy) return;
        IsBusy = true; _cancel = new(); StatusText = "Working…";
        try
        {
            var service = _host.PlateSolving ?? throw new InvalidOperationException("Configure a plate solver first.");
            var rig = Setup ?? throw new InvalidOperationException(_context.NoSetupText);
            await body(service, rig, _cancel.Token);
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { _cancel.Dispose(); _cancel = null; IsBusy = false; RefreshHints(); SlewAndCenterCommand.NotifyCanExecuteChanged(); }
    }
    private void ShowResult(PlateSolveService service, PlateSolveResult result)
    {
        StatusText = result.Success ? "Solved" : result.Message ?? "No solution.";
        if (!result.Success) { ResultText = StatusText; return; }
        var d = PlateSolveDiagnostics.From(service.LastRequest!, result);
        var error = service.LastRequest?.ApproximateCenter is { } mount && result.Center is { } center
            ? SkyMath.DegreesToArcseconds(SkyMath.AngularSeparationDegrees(mount, center)) : (double?)null;
        ResultText = FormattableString.Invariant($"RA {result.Center?.RightAscensionHours:0.######} h · Dec {result.Center?.DeclinationDegrees:0.######}°\nRotation {result.RotationDegrees:0.###}° · Scale {result.PixelScaleArcsecPerPixel:0.###} arcsec/px · FOV {result.FieldOfViewXDegrees:0.###} × {result.FieldOfViewYDegrees:0.###}°\nParity {result.Parity} · Duration {result.Duration.TotalSeconds:0.##} s · Mount error {error:0.##} arcsec\nExpected scale {d.ExpectedScale:0.###} · Solved {d.SolvedScale:0.###} · Difference {d.ScaleDifferencePercent:+0.##;-0.##;0}%\nConfigured focal length {d.ConfiguredFocalLengthMm:0.##} mm · Solved estimate {d.EstimatedFocalLengthMm:0.##} mm · Difference {d.FocalDifferencePercent:+0.##;-0.##;0}%");
    }
    public void Dispose() => _cancel?.Cancel();
}
