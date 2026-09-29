using System;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Runtime.Events;
using Astra.Runtime.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ICamera _camera;
    private readonly StateStore _stateStore;
    private readonly Action<Action> _postToUi;
    private readonly TimeSpan _exposureDuration;
    private readonly IDisposable _subscription;

    /// <param name="postToUi">
    /// Marshals an action onto the UI thread. EventBus handlers run on whichever thread
    /// published the event, so property changes must not happen directly in them.
    /// </param>
    public MainViewModel(
        ICamera camera,
        EventBus eventBus,
        StateStore stateStore,
        Action<Action> postToUi,
        TimeSpan? exposureDuration = null
    )
    {
        _camera = camera;
        _stateStore = stateStore;
        _postToUi = postToUi;
        _exposureDuration = exposureDuration ?? TimeSpan.FromSeconds(5);

        RefreshConnectionState();
        ExposureState = camera.ExposureState;

        _subscription = eventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
    }

    public string CameraName => _camera.Name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    public partial DeviceConnectionState ConnectionState { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    public partial CameraExposureState ExposureState { get; private set; }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => _camera.ConnectAsync();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => _camera.DisconnectAsync();

    [RelayCommand(CanExecute = nameof(CanStartExposure))]
    private async Task StartExposureAsync()
    {
        // The camera switches to Exposing synchronously before its first await.
        var exposure = _camera.ExposeAsync(_exposureDuration);
        ExposureState = _camera.ExposureState;

        try
        {
            await exposure;
        }
        finally
        {
            ExposureState = _camera.ExposureState;
        }
    }

    private bool CanConnect() => ConnectionState == DeviceConnectionState.Disconnected;

    private bool CanDisconnect() => ConnectionState == DeviceConnectionState.Connected;

    private bool CanStartExposure() =>
        ConnectionState == DeviceConnectionState.Connected
        && ExposureState == CameraExposureState.Idle;

    public void Dispose()
    {
        _subscription.Dispose();
    }

    private Task OnConnectionStateChanged(
        DeviceConnectionStateChanged e,
        CancellationToken cancellationToken
    )
    {
        if (e.DeviceId == _camera.Id)
        {
            _postToUi(RefreshConnectionState);
        }

        return Task.CompletedTask;
    }

    private void RefreshConnectionState()
    {
        ConnectionState = _stateStore.TryGet(_camera.Id, out var state)
            ? state!.ConnectionState
            : _camera.ConnectionState;
    }
}
