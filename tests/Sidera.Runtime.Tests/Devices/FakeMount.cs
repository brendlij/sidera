using Sidera.Core.Devices;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Devices;

/// <summary>
/// A mount whose slews the test holds open and releases. It accepts overlapping calls without complaint,
/// so any waiting seen in a test comes from the ResourceManager.
/// </summary>
internal sealed class FakeMount(string id, bool connected = true) : IMount
{
    public sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<int, Gate> _gates = new();
    private int _slewCalls;

    public DeviceId Id { get; } = new(id);
    public string Name => "Fake mount";
    public DeviceType Type => DeviceType.Mount;
    public DeviceConnectionState ConnectionState { get; set; } =
        connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected;
    public MountMotionState MotionState => MountMotionState.Idle;
    public CelestialCoordinates Coordinates { get; private set; } = new(0, 0);

    public int SlewCalls => _slewCalls;
    public bool Block { get; set; }
    public Exception? Failure { get; set; }
    public List<CelestialCoordinates> Targets { get; } = new();

    public Gate GateOf(int call)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(call, out var gate))
            {
                gate = new Gate();
                _gates[call] = gate;
            }

            return gate;
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectionState = DeviceConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectionState = DeviceConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public async Task SlewToAsync(CelestialCoordinates target, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _slewCalls);
        lock (Targets)
        {
            Targets.Add(target);
        }

        var gate = GateOf(call);
        gate.Started.TrySetResult();

        if (Failure is not null)
        {
            throw Failure;
        }

        if (Block)
        {
            await gate.Release.Task.WaitAsync(cancellationToken);
        }

        Coordinates = target;
    }
}
