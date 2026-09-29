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
    private readonly IDisposable _connectionSubscription;
    private readonly IDisposable _exposureSubscription;

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

        RefreshState();

        _connectionSubscription = eventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
        _exposureSubscription = eventBus.Subscribe<CameraExposureStateChanged>(OnExposureStateChanged);
    }

    public string CameraName => _camera.Name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    public partial DeviceConnectionState ConnectionState { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    public partial CameraExposureState ExposureState { get; private set; }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => _camera.ConnectAsync();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => _camera.DisconnectAsync();

    [RelayCommand(CanExecute = nameof(CanStartExposure))]
    private Task StartExposureAsync() => _camera.ExposeAsync(_exposureDuration);

    private bool CanConnect() => ConnectionState == DeviceConnectionState.Disconnected;

    private bool CanDisconnect() =>
        ConnectionState == DeviceConnectionState.Connected
        && ExposureState == CameraExposureState.Idle;

    private bool CanStartExposure() =>
        ConnectionState == DeviceConnectionState.Connected
        && ExposureState == CameraExposureState.Idle;

    public void Dispose()
    {
        _connectionSubscription.Dispose();
        _exposureSubscription.Dispose();
    }

    private Task OnConnectionStateChanged(
        DeviceConnectionStateChanged e,
        CancellationToken cancellationToken
    ) => PostRefreshFor(e.DeviceId);

    private Task OnExposureStateChanged(
        CameraExposureStateChanged e,
        CancellationToken cancellationToken
    ) => PostRefreshFor(e.DeviceId);

    private Task PostRefreshFor(DeviceId deviceId)
    {
        if (deviceId == _camera.Id)
        {
            _postToUi(RefreshState);
        }

        return Task.CompletedTask;
    }

    private void RefreshState()
    {
        if (_stateStore.TryGet(_camera.Id, out var state))
        {
            ConnectionState = state!.ConnectionState;
            ExposureState = state.ExposureState ?? CameraExposureState.Idle;
        }
        else
        {
            ConnectionState = _camera.ConnectionState;
            ExposureState = _camera.ExposureState;
        }
    }
}
