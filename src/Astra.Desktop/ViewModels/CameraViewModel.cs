using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>A camera card: connection, exposure state and progress, and manual exposures.</summary>
public sealed partial class CameraViewModel : DeviceViewModelBase
{
    private readonly ICamera _camera;
    private readonly ImagingViewModel _imaging;
    private readonly TimeSpan _manualExposure;
    private readonly IDisposable _exposureSubscription;
    private CancellationTokenSource? _exposureCts;

    public CameraViewModel(
        ICamera camera,
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        TimeSpan manualExposure
    ) : base(camera, host, postToUi, activity)
    {
        _camera = camera;
        _imaging = imaging;
        _manualExposure = manualExposure;

        _camera.ExposureProgressChanged += OnExposureProgressChanged;
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

    public string ExposureProgressText => ExposureProgress.ToString("P0", CultureInfo.CurrentCulture);

    public string ExposureSummary => IsExposing
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{ExposureElapsed.TotalSeconds:0.0} / {ExposureDuration.TotalSeconds:0.0} s")
        : string.Empty;

    /// <summary>Length of a manual exposure, shown on the button.</summary>
    public string ManualExposureText =>
        string.Create(CultureInfo.InvariantCulture, $"{_manualExposure.TotalSeconds:0.##} s");

    [RelayCommand(CanExecute = nameof(CanStartExposure))]
    private async Task StartExposureAsync()
    {
        var cts = new CancellationTokenSource();
        _exposureCts = cts;
        ClearError();
        IsManualExposureRunning = true;
        RefreshCommands();

        try
        {
            var frame = await Host.DeviceOperations.ExposeAsync(Id, _manualExposure, cts.Token);
            _imaging.Publish(frame, SourceDescription());
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
    }

    // Raised on the camera's thread, about five times a second during an exposure.
    private void OnExposureProgressChanged(object? sender, EventArgs e)
    {
        PostToUi(() =>
        {
            ExposureElapsed = _camera.ExposureElapsed;
            ExposureDuration = _camera.ExposureDuration ?? TimeSpan.Zero;
            ExposureProgress = _camera.ExposureProgress;
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
        _camera.ExposureProgressChanged -= OnExposureProgressChanged;
        _exposureSubscription.Dispose();
        base.Dispose();
    }
}
