using Sidera.Core.Devices;
using Sidera.Core.Guiding;

namespace Sidera.Runtime.Tests.Devices;

/// <summary>
/// A guider whose start, stop, dither and settle commands the test holds open and releases. It accepts overlapping
/// calls without complaint, so any waiting seen in a test comes from the ResourceManager.
/// </summary>
internal sealed class FakeGuider(string id, bool connected = true, bool guiding = false) : IDitherGuider, IGuidingSettler
{
    public sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, Gate> _gates = new();
    private int _connectCalls;
    private int _disconnectCalls;
    private int _startCalls;
    private int _stopCalls;
    private int _ditherCalls;
    private int _settleCalls;

    public DeviceId Id { get; } = new(id);
    public string Name => "Fake guider";
    public DeviceType Type => DeviceType.Guider;
    public DeviceConnectionState ConnectionState { get; set; } =
        connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected;
    public GuidingState GuidingState { get; private set; } = guiding ? GuidingState.Guiding : GuidingState.Idle;

    public int ConnectCalls => _connectCalls;
    public int DisconnectCalls => _disconnectCalls;
    public int StartCalls => _startCalls;
    public int StopCalls => _stopCalls;
    public int DitherCalls => _ditherCalls;
    public int SettleCalls => _settleCalls;
    public List<double> Amplitudes { get; } = new();
    public List<GuidingSettleOptions> SettleOptions { get; } = new();

    /// <summary>When true, start, stop, dither and settle wait on their gate until the test releases it.</summary>
    public bool Block { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>Thrown by settle once its gate is released (or at once, when not blocking).</summary>
    public Exception? SettleFailure { get; set; }

    /// <summary>Gate of the n-th call of an operation ("start", "stop" or "dither"), created on demand.</summary>
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

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCalls);
        ConnectionState = DeviceConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disconnectCalls);
        ConnectionState = DeviceConnectionState.Disconnected;
        GuidingState = GuidingState.Idle;
        return Task.CompletedTask;
    }

    public async Task StartGuidingAsync(CancellationToken cancellationToken = default)
    {
        await Run("start", Interlocked.Increment(ref _startCalls), cancellationToken);
        GuidingState = GuidingState.Guiding;
    }

    public async Task StopGuidingAsync(CancellationToken cancellationToken = default)
    {
        await Run("stop", Interlocked.Increment(ref _stopCalls), cancellationToken);
        GuidingState = GuidingState.Idle;
    }

    public async Task DitherAsync(double amplitudePixels, CancellationToken cancellationToken = default)
    {
        lock (Amplitudes)
        {
            Amplitudes.Add(amplitudePixels);
        }

        await Run("dither", Interlocked.Increment(ref _ditherCalls), cancellationToken);
    }

    public async Task SettleAsync(GuidingSettleOptions options, CancellationToken cancellationToken = default)
    {
        lock (SettleOptions)
        {
            SettleOptions.Add(options);
        }

        await Run("settle", Interlocked.Increment(ref _settleCalls), cancellationToken);

        if (SettleFailure is not null)
        {
            throw SettleFailure;
        }
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
