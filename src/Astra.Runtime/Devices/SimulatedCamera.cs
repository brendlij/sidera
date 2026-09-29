using Astra.Core.Devices;

namespace Astra.Runtime.Devices;

public sealed class SimulatedCamera : ICamera
{
    private readonly object _gate = new();
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private CameraExposureState _exposureState = CameraExposureState.Idle;
    private TimeSpan? _exposureDuration;

    public SimulatedCamera(DeviceId id, string name = "Simulated Camera")
    {
        Id = id;
        Name = name;
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
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            SetConnectionState(DeviceConnectionState.Connected);
        }
        catch
        {
            SetConnectionState(DeviceConnectionState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTransition(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting))
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        finally
        {
            SetConnectionState(DeviceConnectionState.Disconnected);
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
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _exposureState = CameraExposureState.Idle;
            }
        }
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

    private void SetConnectionState(DeviceConnectionState state)
    {
        lock (_gate)
        {
            _connectionState = state;
        }
    }
}
