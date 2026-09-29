using Astra.Core.Devices;
using Astra.Core.Events;

namespace Astra.Runtime.Devices;

public sealed class SimulatedCamera : ICamera
{
    private readonly object _gate = new();
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private CameraExposureState _exposureState = CameraExposureState.Idle;
    private TimeSpan? _exposureDuration;

    private readonly IEventPublisher? _events;

    public SimulatedCamera(
        DeviceId id,
        string name = "Simulated Camera",
        IEventPublisher? events = null
    )
    {
        Id = id;
        Name = name;
        _events = events;
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Camera;

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public CameraExposureState ExposureState
    {
        get { lock (_gate) { return _exposureState; } }
    }

    public TimeSpan? ExposureDuration
    {
        get { lock (_gate) { return _exposureDuration; } }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTransition(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting))
        {
            return;
        }

        try
        {
            await PublishConnectionStateChangedAsync(
                DeviceConnectionState.Disconnected,
                DeviceConnectionState.Connecting,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            await SetConnectionStateAsync(DeviceConnectionState.Connected, cancellationToken);
        }
        catch
        {
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool disconnecting;
        lock (_gate)
        {
            if (_connectionState == DeviceConnectionState.Connected
                && _exposureState == CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("Cannot disconnect while an exposure is running.");
            }

            disconnecting = _connectionState == DeviceConnectionState.Connected;
            if (disconnecting)
            {
                _connectionState = DeviceConnectionState.Disconnecting;
            }
        }

        if (!disconnecting)
        {
            return;
        }

        try
        {
            await PublishConnectionStateChangedAsync(
                DeviceConnectionState.Connected,
                DeviceConnectionState.Disconnecting,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        finally
        {
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
        }
    }

    public async Task ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Camera is not connected.");
            }

            if (_exposureState == CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("An exposure is already running.");
            }

            _exposureState = CameraExposureState.Exposing;
            _exposureDuration = duration;
        }

        try
        {
            await PublishExposureStateChangedAsync(
                CameraExposureState.Idle,
                CameraExposureState.Exposing,
                cancellationToken);
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _exposureState = CameraExposureState.Idle;
            }

            await PublishExposureStateChangedAsync(
                CameraExposureState.Exposing,
                CameraExposureState.Idle,
                CancellationToken.None);
        }
    }

    private Task PublishExposureStateChangedAsync(
        CameraExposureState previous,
        CameraExposureState current,
        CancellationToken cancellationToken
    )
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(
                new CameraExposureStateChanged(Id, previous, current),
                cancellationToken
            );
    }

    private bool TryTransition(DeviceConnectionState from, DeviceConnectionState to)
    {
        lock (_gate)
        {
            if (_connectionState != from)
            {
                return false;
            }

            _connectionState = to;
            return true;
        }
    }

    private async Task SetConnectionStateAsync(
        DeviceConnectionState state,
        CancellationToken cancellationToken
    )
    {
        DeviceConnectionState previous;
        lock (_gate)
        {
            previous = _connectionState;
            _connectionState = state;
        }

        await PublishConnectionStateChangedAsync(previous, state, cancellationToken);
    }

    private Task PublishConnectionStateChangedAsync(
        DeviceConnectionState previous,
        DeviceConnectionState current,
        CancellationToken cancellationToken
    )
    {
        return _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(
                new DeviceConnectionStateChanged(Id, previous, current),
                cancellationToken
            );
    }
}
