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
    private readonly Action<Action> _post;
    private CancellationTokenSource? _cancel;
    public PlateSolveViewModel(SideraRuntimeHost host, ImagingViewModel imaging, SiteService? settings, Action<Action> post)
    {
        _host = host; _imaging = imaging; _settings = settings; _post = post;
        var s = settings?.PlateSolving ?? new(); ExposureSeconds = s.ExposureSeconds;
        ToleranceArcseconds = s.CenteringToleranceArcseconds; MaxAttempts = s.MaxCenteringAttempts;
        RefreshEquipment();
    }
    public ObservableCollection<Rig> Rigs { get; } = [];
    public ObservableCollection<IMount> Mounts { get; } = [];
    [ObservableProperty] public partial Rig? SelectedRig { get; set; }
    [ObservableProperty] public partial IMount? SelectedMount { get; set; }
    [ObservableProperty] public partial double ExposureSeconds { get; set; }
    [ObservableProperty] public partial double RaHours { get; set; }
    [ObservableProperty] public partial double DecDegrees { get; set; }
    [ObservableProperty] public partial double ToleranceArcseconds { get; set; }
    [ObservableProperty] public partial int MaxAttempts { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "Ready";
    [ObservableProperty] public partial string ResultText { get; private set; } = "No solve yet.";
    [ObservableProperty] public partial string HintText { get; private set; } = "Select a rig.";
    public string SolverName => _host.PlateSolving?.Solver.Name ?? "No solver configured";
    public string CameraName => SelectedRig is { } r && _host.DeviceRegistry.TryGet(r.CameraId, out var camera) ? camera!.Name : "Unknown";
    [RelayCommand] public void RefreshEquipment()
    {
        var rigId = SelectedRig?.Id; var mountId = SelectedMount?.Id;
        Rigs.Clear(); foreach (var r in _host.RigRegistry.GetAll()) Rigs.Add(r);
        Mounts.Clear(); foreach (var m in _host.DeviceRegistry.GetAll().OfType<IMount>()) Mounts.Add(m);
        SelectedRig = Rigs.FirstOrDefault(r => r.Id == rigId) ?? Rigs.FirstOrDefault();
        SelectedMount = Mounts.FirstOrDefault(m => m.Id == mountId) ?? Mounts.FirstOrDefault();
        RefreshHints();
    }
    partial void OnSelectedRigChanged(Rig? value) { OnPropertyChanged(nameof(CameraName)); RefreshHints(); }
    partial void OnSelectedMountChanged(IMount? value) => RefreshHints();
    private PlateSolveDefaults Defaults => (_settings?.PlateSolving ?? new()).Defaults();
    private void RefreshHints()
    {
        if (SelectedRig is not { } rig) { HintText = "Select a rig."; return; }
        _host.DeviceRegistry.TryGet(rig.CameraId, out var camera);
        var geometry = OpticalTrainGeometry.Resolve(rig.Optics, SensorGeometry.From((camera as ICameraControl)?.Capabilities.Value));
        CelestialCoordinates? center = null;
        try { if (SelectedMount?.ConnectionState == DeviceConnectionState.Connected) center = SelectedMount.Coordinates; } catch { }
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
            throw new InvalidOperationException("Select the rig whose camera captured the last frame.");
        ShowResult(service, await service.SolveAsync(frame, rig, SelectedMount?.Id, Defaults, cancellationToken: token));
    });
    [RelayCommand] private Task SlewAndCenterAsync() => RunAsync(async (service, rig, token) =>
    {
        var mount = SelectedMount ?? throw new InvalidOperationException("Select a mount.");
        var progress = new Progress<CenteringProgress>(p => _post(() => StatusText = FormattableString.Invariant($"{p.Stage} · attempt {p.Attempt} · error {p.PointingErrorArcseconds:0.##} arcsec")));
        var result = await service.CenterTargetAsync(new(RaHours, DecDegrees), rig, mount.Id, ToleranceArcseconds, MaxAttempts,
            TimeSpan.FromSeconds(ExposureSeconds), Defaults, progress: progress, cancellationToken: token);
        if (service.LastResult is { } solve) ShowResult(service, solve);
        StatusText = result.Success ? "Centered" : result.Message ?? "Centering failed.";
    });
    /// <summary>Explicit only: tells the mount where the last solve found it. Never done by a solve, a sequence or centering.</summary>
    [RelayCommand] private Task SyncMountAsync() => RunAsync(async (service, rig, token) =>
    {
        var mount = SelectedMount ?? throw new InvalidOperationException("Select a mount.");
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
            var rig = SelectedRig ?? throw new InvalidOperationException("Select a rig.");
            await body(service, rig, _cancel.Token);
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { _cancel.Dispose(); _cancel = null; IsBusy = false; RefreshHints(); }
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
