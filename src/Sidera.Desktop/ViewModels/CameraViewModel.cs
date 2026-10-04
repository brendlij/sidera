using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>A camera card: connection, exposure state and progress, and manual exposures.</summary>
public sealed partial class CameraViewModel : DeviceViewModelBase
{
    private readonly ICamera _camera;
    private readonly ImagingViewModel _imaging;
    private readonly IDisposable _exposureSubscription;
    private CancellationTokenSource? _exposureCts;

    public CameraViewModel(
        ICamera camera,
        SideraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        TimeSpan manualExposure
    ) : base(camera, host, postToUi, activity)
    {
        _camera = camera;
        _imaging = imaging;
        ExposureInput = Format(manualExposure);

        _camera.ExposureProgressChanged += OnExposureProgressChanged;
        if (camera is ICameraControl control)
        {
            control.CapabilitiesChanged += OnCapabilitiesChanged;
        }

        _exposureSubscription = host.EventBus.Subscribe<CameraExposureStateChanged>((e, _) =>
        {
            if (e.DeviceId == camera.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });

        Refresh();
    }

    public DeviceId CameraId => _camera.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExposing))]
    [NotifyPropertyChangedFor(nameof(ExposureSummary))]
    public partial CameraExposureState ExposureState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureSummary))]
    public partial TimeSpan ExposureElapsed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureSummary))]
    public partial TimeSpan ExposureDuration { get; private set; }

    /// <summary>From 0.0 to 1.0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureProgressText))]
    public partial double ExposureProgress { get; private set; }

    /// <summary>True while an exposure started with the manual command is running or waiting for the camera.</summary>
    [ObservableProperty]
    public partial bool IsManualExposureRunning { get; private set; }

    public bool IsExposing => ExposureState == CameraExposureState.Exposing;

    private CameraCapabilities? Capabilities => (_camera as ICameraControl)?.Capabilities.Value;

    /// <summary>The camera can end an exposure early and keep the image (it said so; nothing is guessed).</summary>
    public bool SupportsStop => Capabilities?.CanStopExposure == true;

    /// <summary>The camera can throw an exposure away. Without it, cancelling only ends the waiting of Sidera.</summary>
    public bool SupportsAbort => Capabilities?.CanAbortExposure == true;

    public string CancelButtonText => SupportsAbort ? "Abort Exposure" : "Cancel Exposure";

    private void OnCapabilitiesChanged(object? sender, EventArgs e) => PostToUi(() =>
    {
        OnPropertyChanged(nameof(SupportsStop));
        OnPropertyChanged(nameof(SupportsAbort));
        OnPropertyChanged(nameof(CancelButtonText));
        RefreshCommands();
    });

    public string ExposureProgressText => ExposureProgress.ToString("P0", CultureInfo.CurrentCulture);

    public string ExposureSummary => IsExposing
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{ExposureElapsed.TotalSeconds:0.0} / {ExposureDuration.TotalSeconds:0.0} s")
        : string.Empty;

    /// <summary>What the last manual exposure of this camera produced, or that there was none yet; it is on the Imaging page.</summary>
    [ObservableProperty]
    public partial string LastFrameText { get; private set; } = "No manual exposure yet";

    /// <summary>Length of a manual exposure, shown on the button.</summary>
    public string ManualExposureText => $"{ExposureInput.Trim()} s";

    /// <summary>The length of the next manual exposure, in seconds, as typed by the user.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualExposureText))]
    public partial string ExposureInput { get; set; } = string.Empty;

    private static string Format(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    [RelayCommand(CanExecute = nameof(CanStartExposure))]
    private async Task StartExposureAsync()
    {
        var text = ExposureInput?.Trim();
        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            || !double.IsFinite(seconds) || seconds <= 0)
        {
            ReportError(new FormatException("Exposure must be a number of seconds greater than 0."));
            return;
        }

        var cts = new CancellationTokenSource();
        _exposureCts = cts;
        ClearError();
        IsManualExposureRunning = true;
        RefreshCommands();

        try
        {
            var frame = await Host.DeviceOperations.ExposeAsync(Id, TimeSpan.FromSeconds(seconds), cts.Token);
            _imaging.Publish(frame, SourceDescription());
            LastFrameText = frame.Acquisition is { Stopped: true } stopped
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stopped after about {stopped.StoppedAfter?.TotalSeconds ?? 0:0.#} s of {frame.ExposureDuration.TotalSeconds:0.###} s, shown on the Imaging page")
                : string.Create(CultureInfo.InvariantCulture, $"{frame.ExposureDuration.TotalSeconds:0.###} s exposure, shown on the Imaging page");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the user: the camera is idle again and no frame was produced.
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            _exposureCts = null;
            cts.Dispose();
            IsManualExposureRunning = false;
            Refresh();
        }
    }

    // Ends the exposure early and keeps the image: the manual exposure that is waiting for the camera gets it. Not the same as aborting.
    [RelayCommand(CanExecute = nameof(CanStopExposure))]
    private async Task StopExposureAsync()
    {
        if (_camera is not ICameraControl control)
        {
            return;
        }

        try
        {
            await control.StopExposureAsync();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private bool CanStopExposure() => SupportsStop && IsManualExposureRunning && IsExposing;

    [RelayCommand(CanExecute = nameof(IsManualExposureRunning))]
    private void CancelExposure()
    {
        try
        {
            _exposureCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The exposure just ended.
        }
    }

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && ExposureState == CameraExposureState.Idle && !IsManualExposureRunning;

    private bool CanStartExposure() =>
        !IsSequenceRunning && IsConnected && ExposureState == CameraExposureState.Idle && !IsManualExposureRunning;

    protected override void RefreshDeviceState()
    {
        ExposureState = StateStore.TryGet(Id, out var state) && state?.ExposureState is { } exposure
            ? exposure
            : _camera.ExposureState;
        ExposureElapsed = _camera.ExposureElapsed;
        ExposureDuration = _camera.ExposureDuration ?? TimeSpan.Zero;
        ExposureProgress = _camera.ExposureProgress;
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        StartExposureCommand.NotifyCanExecuteChanged();
        CancelExposureCommand.NotifyCanExecuteChanged();
        StopExposureCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() => IsExposing
        ? new DeviceActivity(
            string.Create(CultureInfo.InvariantCulture, $"Exposing {ExposureElapsed.TotalSeconds:0.#} / {ExposureDuration.TotalSeconds:0.#} s"),
            ExposureProgress, true)
        : new DeviceActivity("Idle");

    // Raised on the camera's thread, about five times a second during an exposure.
    private void OnExposureProgressChanged(object? sender, EventArgs e)
    {
        PostToUi(() =>
        {
            ExposureElapsed = _camera.ExposureElapsed;
            ExposureDuration = _camera.ExposureDuration ?? TimeSpan.Zero;
            ExposureProgress = _camera.ExposureProgress;
            RefreshSummary();
        });
    }

    private string SourceDescription()
    {
        var rig = Host.RigRegistry.GetAll().FirstOrDefault(r => r.CameraId == Id);
        return rig is null ? $"{Name} (manual)" : $"{Name} · {rig.Name} (manual)";
    }

    public override void Dispose()
    {
        CancelExposure();
        if (_camera is ICameraControl control)
        {
            control.CapabilitiesChanged -= OnCapabilitiesChanged;
        }

        _camera.ExposureProgressChanged -= OnExposureProgressChanged;
        _exposureSubscription.Dispose();
        base.Dispose();
    }
}
