using System.Diagnostics;
using Astra.Core.Devices;
using Astra.Core.Events;

namespace Astra.Runtime.Devices;

public sealed class SimulatedCamera : ICamera
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private TimeSpan _exposureElapsed;
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

    public TimeSpan ExposureElapsed
    {
        get { lock (_gate) { return _exposureElapsed; } }
    }

    public double ExposureProgress
    {
        get
        {
            lock (_gate)
            {
                return _exposureDuration is { } duration && duration > TimeSpan.Zero
                    ? Math.Clamp(_exposureElapsed / duration, 0.0, 1.0)
                    : 0.0;
            }
        }
    }

    public event EventHandler? ExposureProgressChanged;

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
            _exposureElapsed = TimeSpan.Zero;
        }

        var stopwatch = Stopwatch.StartNew();
        var completed = false;

        try
        {
            await PublishExposureStateChangedAsync(
                CameraExposureState.Idle,
                CameraExposureState.Exposing,
                cancellationToken);
            RaiseExposureProgressChanged();

            while (stopwatch.Elapsed < duration)
            {
                var remaining = duration - stopwatch.Elapsed;
                await Task.Delay(
                    remaining < ProgressInterval ? remaining : ProgressInterval,
                    cancellationToken);

                if (stopwatch.Elapsed < duration)
                {
                    SetElapsed(stopwatch.Elapsed);
                }
            }

            SetElapsed(duration);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                // Cancelled: keep the portion that actually elapsed instead of jumping to 100%.
                SetElapsed(stopwatch.Elapsed < duration ? stopwatch.Elapsed : duration);
            }

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

    private void SetElapsed(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _exposureElapsed = elapsed;
        }

        RaiseExposureProgressChanged();
    }

    private void RaiseExposureProgressChanged()
    {
        // Progress observers must not be able to break an exposure.
        foreach (var handler in ExposureProgressChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch
            {
            }
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
