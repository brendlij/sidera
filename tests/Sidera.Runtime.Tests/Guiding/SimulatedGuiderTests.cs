using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Guiding;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;
using Sidera.Runtime.State;

namespace Sidera.Runtime.Tests.Guiding;

public class SimulatedGuiderTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private static readonly DeviceId GuiderId = new("guider.main");

    /// <summary>
    /// Records every published event in order. A hook can observe, hold or fail a publication, which lets
    /// tests act exactly while a transition is in progress instead of relying on timing.
    /// </summary>
    private sealed class Recorder : IEventPublisher
    {
        private readonly List<ISideraEvent> _events = new();

        public Func<ISideraEvent, CancellationToken, Task>? OnPublish { get; set; }

        public ISideraEvent[] Events
        {
            get { lock (_events) { return _events.ToArray(); } }
        }

        public async Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default)
            where TEvent : ISideraEvent
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_events)
            {
                _events.Add(sideraEvent);
            }

            if (OnPublish is { } hook)
            {
                await hook(sideraEvent, cancellationToken);
            }
        }

        /// <summary>Completes when <paramref name="expected"/> is published; the publication then continues.</summary>
        public Task Reached(ISideraEvent expected)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnPublish = (e, _) =>
            {
                if (e.Equals(expected))
                {
                    reached.TrySetResult();
                }

                return Task.CompletedTask;
            };
            return reached.Task;
        }

        /// <summary>
        /// Holds the publication of <paramref name="expected"/> until its token is cancelled. The returned task
        /// completes once the publication is being held.
        /// </summary>
        public Task Hold(ISideraEvent expected)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnPublish = async (e, ct) =>
            {
                if (e.Equals(expected))
                {
                    held.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            };
            return held.Task;
        }

        /// <summary>
        /// Holds the publication of <paramref name="expected"/> until <paramref name="release"/> completes,
        /// regardless of its token. The returned task completes once the publication is being held.
        /// </summary>
        public Task HoldUntil(ISideraEvent expected, Task release)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnPublish = async (e, _) =>
            {
                if (e.Equals(expected))
                {
                    held.TrySetResult();
                    await release;
                }
            };
            return held.Task;
        }

        /// <summary>
        /// Cancels <paramref name="source"/> while <paramref name="expected"/> is being published and then lets
        /// the publication return normally, like a final <c>EventBus</c> subscriber would.
        /// </summary>
        public void CancelDuring(ISideraEvent expected, CancellationTokenSource source)
        {
            OnPublish = (e, _) =>
            {
                if (e.Equals(expected))
                {
                    source.Cancel();
                }

                return Task.CompletedTask;
            };
        }
    }

    private static GuidingStateChanged Guiding(GuidingState from, GuidingState to) => new(GuiderId, from, to);

    private static DeviceConnectionStateChanged Connection(DeviceConnectionState from, DeviceConnectionState to) =>
        new(GuiderId, from, to);

    private static readonly ISideraEvent[] ConnectEvents =
    [
        Connection(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting),
        Connection(DeviceConnectionState.Connecting, DeviceConnectionState.Connected),
    ];

    private static readonly ISideraEvent[] StartEvents =
    [
        Guiding(GuidingState.Idle, GuidingState.Starting),
        Guiding(GuidingState.Starting, GuidingState.Guiding),
    ];

    private static readonly ISideraEvent[] StopEvents =
    [
        Guiding(GuidingState.Guiding, GuidingState.Stopping),
        Guiding(GuidingState.Stopping, GuidingState.Idle),
    ];

    private static readonly ISideraEvent[] DitherEvents =
    [
        Guiding(GuidingState.Guiding, GuidingState.Dithering),
        Guiding(GuidingState.Dithering, GuidingState.Guiding),
    ];

    private static SimulatedGuider Create(
        IEventPublisher? events = null,
        TimeSpan? start = null,
        TimeSpan? stop = null,
        TimeSpan? dither = null
    ) =>
        new(GuiderId, events: events, startDuration: start ?? Quick, stopDuration: stop ?? Quick,
            ditherDuration: dither ?? Quick);

    private static async Task<SimulatedGuider> CreateGuiding(Recorder recorder, TimeSpan? dither = null)
    {
        var guider = Create(recorder, dither: dither);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        return guider;
    }

    private static void AssertState(SimulatedGuider guider, DeviceConnectionState connection, GuidingState guiding)
    {
        Assert.Equal(connection, guider.ConnectionState);
        Assert.Equal(guiding, guider.GuidingState);
    }

    // Identity and options

    [Fact]
    public void NewGuider_HasIdentity_AndStartsDisconnectedAndIdle()
    {
        var guider = new SimulatedGuider(GuiderId);

        Assert.Equal(GuiderId, guider.Id);
        Assert.Equal("Simulated Guider", guider.Name);
        Assert.Equal(DeviceType.Guider, guider.Type);
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Equal("Off-axis", new SimulatedGuider(GuiderId, "Off-axis").Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeDurations_AreRejected(int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedGuider(GuiderId, startDuration: duration));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedGuider(GuiderId, stopDuration: duration));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedGuider(GuiderId, ditherDuration: duration));
    }

    // Successful lifecycle

    [Fact]
    public async Task FullLifecycle_PublishesExactTransitions()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);

        await guider.ConnectAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        await guider.StartGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        await guider.StopGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        await guider.DisconnectAsync();
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);

        Assert.Equal(
            [
                .. ConnectEvents,
                .. StartEvents,
                .. StopEvents,
                Connection(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting),
                Connection(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected),
            ],
            recorder.Events);
    }

    [Fact]
    public async Task RepeatedCallsInTheirTargetState_AreNoOps_WithoutDuplicateEvents()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);

        await guider.DisconnectAsync();
        await guider.ConnectAsync();
        await guider.ConnectAsync();
        await guider.StopGuidingAsync();
        await guider.StartGuidingAsync();
        await guider.StartGuidingAsync();
        await guider.StopGuidingAsync();
        await guider.StopGuidingAsync();

        Assert.Equal([.. ConnectEvents, .. StartEvents, .. StopEvents], recorder.Events);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    [Fact]
    public async Task StartAndStop_WhileDisconnected_Fail_WithoutConnecting()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);

        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StartGuidingAsync());
        var stop = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StopGuidingAsync());

        Assert.Contains("'guider.main' is not connected", start.Message);
        Assert.Contains("'guider.main' is not connected", stop.Message);
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task CompletedStart_KeepsGuiding_AfterItsTokenIsCancelled()
    {
        var guider = Create();
        await guider.ConnectAsync();
        using var cts = new CancellationTokenSource();

        await guider.StartGuidingAsync(cts.Token);
        await cts.CancelAsync();

        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
    }

    [Fact]
    public async Task DisconnectWhileGuiding_PublishesGuidingIdleBeforeDisconnected()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();

        await guider.DisconnectAsync();

        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Equal(
            [
                .. ConnectEvents,
                .. StartEvents,
                Connection(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting),
                Guiding(GuidingState.Guiding, GuidingState.Idle),
                Connection(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected),
            ],
            recorder.Events);
    }

    // Cancellation

    [Fact]
    public async Task AlreadyCancelledCalls_ChangeNothing_AndPublishNothing()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Disconnected: connect would act, disconnect would be a no-op.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.ConnectAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.DisconnectAsync(cts.Token));
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Empty(recorder.Events);

        // Connected and idle: start would act; connect and stop would be no-ops.
        await guider.ConnectAsync();
        var before = recorder.Events.Length;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.StartGuidingAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.ConnectAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.StopGuidingAsync(cts.Token));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        Assert.Equal(before, recorder.Events.Length);

        // Guiding: stop and disconnect would act, start would be a no-op.
        await guider.StartGuidingAsync();
        before = recorder.Events.Length;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.StopGuidingAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.DisconnectAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.StartGuidingAsync(cts.Token));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal(before, recorder.Events.Length);
    }

    [Fact]
    public async Task CancellationDuringConnect_ReturnsToDisconnected()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        using var cts = new CancellationTokenSource();
        var held = recorder.Hold(ConnectEvents[0]);

        var connect = guider.ConnectAsync(cts.Token);
        await held.WaitAsync(Bound);
        Assert.Equal(DeviceConnectionState.Connecting, guider.ConnectionState);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Equal(
            [
                ConnectEvents[0],
                Connection(DeviceConnectionState.Connecting, DeviceConnectionState.Disconnected),
            ],
            recorder.Events);
    }

    [Fact]
    public async Task CancellationDuringStart_RestoresIdle()
    {
        var recorder = new Recorder();
        var guider = Create(recorder, start: Long);
        await guider.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var starting = recorder.Reached(StartEvents[0]);

        var start = guider.StartGuidingAsync(cts.Token);
        await starting.WaitAsync(Bound);
        Assert.Equal(GuidingState.Starting, guider.GuidingState);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        Assert.Equal(
            [.. ConnectEvents, StartEvents[0], Guiding(GuidingState.Starting, GuidingState.Idle)],
            recorder.Events);
    }

    [Fact]
    public async Task CancellationDuringStop_RestoresGuiding()
    {
        var recorder = new Recorder();
        var guider = Create(recorder, stop: Long);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        using var cts = new CancellationTokenSource();
        var stopping = recorder.Reached(StopEvents[0]);

        var stop = guider.StopGuidingAsync(cts.Token);
        await stopping.WaitAsync(Bound);
        Assert.Equal(GuidingState.Stopping, guider.GuidingState);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal(
            [.. ConnectEvents, .. StartEvents, StopEvents[0], Guiding(GuidingState.Stopping, GuidingState.Guiding)],
            recorder.Events);
    }

    [Fact]
    public async Task CancellationDuringDisconnect_FinishesCleanup_ThenPropagates()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        using var cts = new CancellationTokenSource();
        var disconnecting = Connection(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting);
        var held = recorder.Hold(disconnecting);

        var disconnect = guider.DisconnectAsync(cts.Token);
        await held.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Equal(
            [
                .. ConnectEvents,
                .. StartEvents,
                disconnecting,
                Guiding(GuidingState.Guiding, GuidingState.Idle),
                Connection(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected),
            ],
            recorder.Events);
    }

    [Fact]
    public async Task CancellationDuringDisconnectCleanup_IsPropagated_AfterCleanupCompletes()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        using var cts = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guidingIdle = Guiding(GuidingState.Guiding, GuidingState.Idle);
        var held = recorder.HoldUntil(guidingIdle, release.Task);

        var disconnect = guider.DisconnectAsync(cts.Token);
        await held.WaitAsync(Bound);
        await cts.CancelAsync();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Equal(
            [
                .. ConnectEvents,
                .. StartEvents,
                Connection(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting),
                guidingIdle,
                Connection(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected),
            ],
            recorder.Events);

        recorder.OnPublish = null;
        await guider.ConnectAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    [Fact]
    public async Task CancellationDuringFinalConnectPublication_ReturnsToDisconnected()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        using var cts = new CancellationTokenSource();
        recorder.CancelDuring(ConnectEvents[1], cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.ConnectAsync(cts.Token));

        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Equal(
            [.. ConnectEvents, Connection(DeviceConnectionState.Connected, DeviceConnectionState.Disconnected)],
            recorder.Events);

        recorder.OnPublish = null;
        await guider.ConnectAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    [Fact]
    public async Task CancellationDuringFinalStartPublication_RestoresIdle()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        using var cts = new CancellationTokenSource();
        recorder.CancelDuring(StartEvents[1], cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.StartGuidingAsync(cts.Token));

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        Assert.Equal(
            [.. ConnectEvents, .. StartEvents, Guiding(GuidingState.Guiding, GuidingState.Idle)],
            recorder.Events);

        recorder.OnPublish = null;
        await guider.StartGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
    }

    [Fact]
    public async Task CancellationDuringFinalStopPublication_RestoresGuiding()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        using var cts = new CancellationTokenSource();
        recorder.CancelDuring(StopEvents[1], cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.StopGuidingAsync(cts.Token));

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal(
            [.. ConnectEvents, .. StartEvents, .. StopEvents, Guiding(GuidingState.Idle, GuidingState.Guiding)],
            recorder.Events);

        recorder.OnPublish = null;
        await guider.StopGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    // Overlapping operations

    [Fact]
    public async Task OverlappingCalls_AreRejected_WhileStarting()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var held = recorder.Hold(StartEvents[0]);

        var start = guider.StartGuidingAsync(cts.Token);
        await held.WaitAsync(Bound);

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StartGuidingAsync());
        Assert.Contains("'guider.main' is busy", again.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StopGuidingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DisconnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.ConnectAsync());
        Assert.Equal(GuidingState.Starting, guider.GuidingState);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    [Fact]
    public async Task OverlappingCalls_AreRejected_WhileStopping_AndWhileConnecting()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        using var connecting = new CancellationTokenSource();
        var held = recorder.Hold(ConnectEvents[0]);

        var connect = guider.ConnectAsync(connecting.Token);
        await held.WaitAsync(Bound);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.ConnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DisconnectAsync());
        await connecting.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Bound));

        recorder.OnPublish = null;
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        using var stopping = new CancellationTokenSource();
        held = recorder.Hold(StopEvents[0]);

        var stop = guider.StopGuidingAsync(stopping.Token);
        await held.WaitAsync(Bound);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StopGuidingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StartGuidingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DisconnectAsync());
        Assert.Equal(GuidingState.Stopping, guider.GuidingState);

        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
    }

    // Recovery

    [Fact]
    public async Task OperationsWork_AfterCancellation()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var held = recorder.Hold(StartEvents[0]);
        var start = guider.StartGuidingAsync(cts.Token);
        await held.WaitAsync(Bound);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Bound));
        recorder.OnPublish = null;

        await guider.StartGuidingAsync();
        await guider.StopGuidingAsync();
        await guider.DisconnectAsync();

        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
    }

    [Fact]
    public async Task PublicationFailure_RestoresTheStableState_AndReleasesTheGuard()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);
        await guider.ConnectAsync();
        recorder.OnPublish = (e, _) => e.Equals(StartEvents[1])
            ? throw new InvalidOperationException("publisher broke")
            : Task.CompletedTask;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StartGuidingAsync());

        Assert.Equal("publisher broke", error.Message);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        Assert.Equal(Guiding(GuidingState.Guiding, GuidingState.Idle), recorder.Events[^1]);

        recorder.OnPublish = (e, _) => e.Equals(StopEvents[0])
            ? throw new InvalidOperationException("publisher broke")
            : Task.CompletedTask;
        await guider.StartGuidingAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StopGuidingAsync());
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);

        recorder.OnPublish = null;
        await guider.StopGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    // Publishers

    [Fact]
    public async Task WorksWithoutAPublisher()
    {
        var guider = Create();

        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        await guider.StopGuidingAsync();
        await guider.StartGuidingAsync();
        await guider.DisconnectAsync();

        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
    }

    [Fact]
    public async Task ThrowingSubscribers_DoNotBreakOperations_ThroughTheEventBus()
    {
        var failures = new List<EventHandlerFailure>();
        var bus = new EventBus(failure =>
        {
            lock (failures)
            {
                failures.Add(failure);
            }
        });
        bus.Subscribe<GuidingStateChanged>((_, _) => throw new InvalidOperationException("bad subscriber"));
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) => throw new InvalidOperationException("bad subscriber"));
        var guider = Create(bus);

        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        await guider.StopGuidingAsync();
        await guider.StartGuidingAsync();
        await guider.DisconnectAsync();

        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        lock (failures)
        {
            Assert.Equal(11, failures.Count);
        }
    }

    // Dithering

    [Fact]
    public void Guider_SupportsDithering()
    {
        Assert.IsAssignableFrom<IDitherGuider>(new SimulatedGuider(GuiderId));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    public async Task Dither_RejectsInvalidAmplitudes_WithoutChangingState(double amplitude)
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        var before = recorder.Events.Length;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => guider.DitherAsync(amplitude));

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal(before, recorder.Events.Length);
    }

    [Fact]
    public async Task Dither_PublishesGuidingDitheringGuiding()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = recorder.HoldUntil(DitherEvents[0], release.Task);

        var dither = guider.DitherAsync(1.5);
        await held.WaitAsync(Bound);
        Assert.Equal(GuidingState.Dithering, guider.GuidingState);
        Assert.False(dither.IsCompleted);
        Assert.Equal([.. ConnectEvents, .. StartEvents, DitherEvents[0]], recorder.Events);
        release.SetResult();
        await dither.WaitAsync(Bound);

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal([.. ConnectEvents, .. StartEvents, .. DitherEvents], recorder.Events);
    }

    [Fact]
    public async Task Dither_WhileDisconnectedOrIdle_Fails_WithoutConnectingOrStarting()
    {
        var recorder = new Recorder();
        var guider = Create(recorder);

        var disconnected = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));
        Assert.Contains("'guider.main' is not connected", disconnected.Message);
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
        Assert.Empty(recorder.Events);

        await guider.ConnectAsync();
        var idle = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));
        Assert.Contains("'guider.main' is not guiding", idle.Message);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
        Assert.Equal(ConnectEvents, recorder.Events);
    }

    [Fact]
    public async Task AlreadyCancelledDither_ChangesNothing_AndPublishesNothing()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        var before = recorder.Events.Length;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.DitherAsync(1, cts.Token));

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal(before, recorder.Events.Length);
    }

    [Fact]
    public async Task OverlappingCalls_AreRejected_WhileDithering()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        using var cts = new CancellationTokenSource();
        var held = recorder.Hold(DitherEvents[0]);

        var dither = guider.DitherAsync(1, cts.Token);
        await held.WaitAsync(Bound);

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));
        Assert.Contains("'guider.main' is busy", again.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StartGuidingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.StopGuidingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DisconnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.ConnectAsync());
        Assert.Equal(GuidingState.Dithering, guider.GuidingState);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dither.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
    }

    [Fact]
    public async Task Dither_IsRejected_WhileAnotherOperationRuns()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        using var cts = new CancellationTokenSource();
        var held = recorder.Hold(StopEvents[0]);

        var stop = guider.StopGuidingAsync(cts.Token);
        await held.WaitAsync(Bound);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));
        Assert.Contains("'guider.main' is busy", error.Message);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
    }

    [Fact]
    public async Task CancellationDuringDither_RestoresGuiding_AndAllowsLaterOperations()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder, dither: Long);
        using var cts = new CancellationTokenSource();
        var dithering = recorder.Reached(DitherEvents[0]);

        var dither = guider.DitherAsync(2, cts.Token);
        await dithering.WaitAsync(Bound);
        Assert.Equal(GuidingState.Dithering, guider.GuidingState);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dither.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal([.. ConnectEvents, .. StartEvents, .. DitherEvents], recorder.Events);

        recorder.OnPublish = null;
        await guider.StopGuidingAsync();
        await guider.DisconnectAsync();
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
    }

    [Fact]
    public async Task CancellationDuringFinalDitherPublication_KeepsGuiding_AndAllowsLaterDithers()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        using var cts = new CancellationTokenSource();
        recorder.CancelDuring(DitherEvents[1], cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.DitherAsync(1, cts.Token));

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal([.. ConnectEvents, .. StartEvents, .. DitherEvents], recorder.Events);

        recorder.OnPublish = null;
        await guider.DitherAsync(1);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
    }

    [Fact]
    public async Task DitherPublicationFailure_RestoresGuiding_AndReleasesTheGuard()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        recorder.OnPublish = (e, _) => e.Equals(DitherEvents[0])
            ? throw new InvalidOperationException("publisher broke")
            : Task.CompletedTask;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));

        Assert.Equal("publisher broke", error.Message);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal([.. ConnectEvents, .. StartEvents, .. DitherEvents], recorder.Events);

        recorder.OnPublish = (e, _) => e.Equals(DitherEvents[1])
            ? throw new InvalidOperationException("publisher broke")
            : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);

        recorder.OnPublish = null;
        await guider.DitherAsync(1);
        await guider.StopGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    /// <summary>Forwards publications to an inner publisher unless <see cref="FailBeforeForwarding"/> throws first.</summary>
    private sealed class Forwarder(IEventPublisher inner) : IEventPublisher
    {
        public Func<ISideraEvent, Exception?>? FailBeforeForwarding { get; set; }

        public Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default)
            where TEvent : ISideraEvent
        {
            if (FailBeforeForwarding?.Invoke(sideraEvent) is { } failure)
            {
                throw failure;
            }

            return inner.PublishAsync(sideraEvent, cancellationToken);
        }
    }

    [Fact]
    public async Task FinalDitherPublicationFailure_RestoresGuiding_InTheSimulatorAndTheStateStore()
    {
        var bus = new EventBus();
        using var store = new StateStore(bus);
        var forwarder = new Forwarder(bus);
        var guider = Create(forwarder);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        var failed = false;
        forwarder.FailBeforeForwarding = e =>
        {
            if (failed || !e.Equals(DitherEvents[1]))
            {
                return null;
            }

            failed = true;
            return new InvalidOperationException("publisher broke");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));

        Assert.Equal("publisher broke", error.Message);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.True(store.TryGet(GuiderId, out var state));
        Assert.Equal(GuidingState.Guiding, state!.GuidingState);

        await guider.DitherAsync(1);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
    }

    [Fact]
    public async Task CleanupPublicationFailure_AfterDitherPublicationFailure_PreservesTheOriginalFailure()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder);
        recorder.OnPublish = (e, _) =>
            e.Equals(DitherEvents[0]) ? throw new InvalidOperationException("publisher broke")
            : e.Equals(DitherEvents[1]) ? throw new InvalidOperationException("cleanup broke")
            : Task.CompletedTask;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.DitherAsync(1));

        Assert.Equal("publisher broke", error.Message);
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal([.. ConnectEvents, .. StartEvents, .. DitherEvents], recorder.Events);

        recorder.OnPublish = null;
        await guider.DitherAsync(1);
        await guider.StopGuidingAsync();
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Idle);
    }

    [Fact]
    public async Task CleanupPublicationFailure_AfterDitherCancellation_PreservesTheCancellation()
    {
        var recorder = new Recorder();
        var guider = await CreateGuiding(recorder, dither: Long);
        using var cts = new CancellationTokenSource();
        var dithering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.OnPublish = (e, _) =>
        {
            if (e.Equals(DitherEvents[0]))
            {
                dithering.TrySetResult();
            }

            return e.Equals(DitherEvents[1])
                ? throw new InvalidOperationException("cleanup broke")
                : Task.CompletedTask;
        };

        var dither = guider.DitherAsync(1, cts.Token);
        await dithering.Task.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dither.WaitAsync(Bound));
        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        Assert.Equal([.. ConnectEvents, .. StartEvents, .. DitherEvents], recorder.Events);

        recorder.OnPublish = null;
        await guider.DitherAsync(1);
        await guider.DisconnectAsync();
        AssertState(guider, DeviceConnectionState.Disconnected, GuidingState.Idle);
    }

    [Fact]
    public async Task ThrowingSubscribers_DoNotBreakDithering_ThroughTheEventBus()
    {
        var failures = new List<EventHandlerFailure>();
        var bus = new EventBus(failure =>
        {
            lock (failures)
            {
                failures.Add(failure);
            }
        });
        bus.Subscribe<GuidingStateChanged>((_, _) => throw new InvalidOperationException("bad subscriber"));
        var guider = Create(bus);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();

        await guider.DitherAsync(1);
        await guider.DitherAsync(1);

        AssertState(guider, DeviceConnectionState.Connected, GuidingState.Guiding);
        lock (failures)
        {
            Assert.Equal(6, failures.Count); // two for the start, two per dither
        }
    }
}
