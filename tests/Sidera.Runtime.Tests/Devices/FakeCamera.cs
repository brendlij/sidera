using Sidera.Core.Devices;

namespace Sidera.Runtime.Tests.Devices;

/// <summary>
/// A camera whose operations can be held open and released by the test, and that never rejects an
/// overlapping call itself. Any waiting seen in a test therefore comes from the ResourceManager.
/// </summary>
internal sealed class FakeCamera(string id) : ICamera
{
    public sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, Gate> _gates = new();
    private int _connectCalls;
    private int _disconnectCalls;
    private int _exposeCalls;

    public DeviceId Id { get; } = new(id);
    public string Name => "Fake camera";
    public DeviceType Type => DeviceType.Camera;
    public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;

    public int ConnectCalls => _connectCalls;
    public int DisconnectCalls => _disconnectCalls;
    public int ExposeCalls => _exposeCalls;

    /// <summary>When true, the next operations wait on their gate until the test releases it.</summary>
    public bool Block { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>Gate of the n-th call of an operation ("connect", "disconnect" or "expose"), created on demand.</summary>
    public Gate GateOf(string operation, int call)
    {
        lock (_gates)
        {
            var key = $"{operation}#{call}";
            if (!_gates.TryGetValue(key, out var gate))
            {
                gate = new Gate();
                _gates[key] = gate;
            }

            return gate;
        }
    }

    public CameraExposureState ExposureState => CameraExposureState.Idle;
    public TimeSpan? ExposureDuration => null;
    public TimeSpan ExposureElapsed => TimeSpan.Zero;
    public double ExposureProgress => 0;

    public event EventHandler? ExposureProgressChanged
    {
        add { }
        remove { }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Run("connect", Interlocked.Increment(ref _connectCalls), cancellationToken);
        ConnectionState = DeviceConnectionState.Connected;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await Run("disconnect", Interlocked.Increment(ref _disconnectCalls), cancellationToken);
        ConnectionState = DeviceConnectionState.Disconnected;
    }

    public async Task<CameraFrame> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await Run("expose", Interlocked.Increment(ref _exposeCalls), cancellationToken);
        return new CameraFrame(2, 2, new ushort[4], duration);
    }

    private async Task Run(string operation, int call, CancellationToken cancellationToken)
    {
        var gate = GateOf(operation, call);
        gate.Started.TrySetResult();

        if (Failure is not null)
        {
            throw Failure;
        }

        if (Block)
        {
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }
}
