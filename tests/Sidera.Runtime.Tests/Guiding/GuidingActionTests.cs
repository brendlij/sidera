using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.Guiding;

public class GuidingActionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);

    private static readonly DeviceId GuiderId = new("guider.main");
    private static readonly ResourceId GuiderResource = ResourceId.ForDevice(GuiderId);

    private sealed class NotAGuider : IDevice
    {
        public DeviceId Id { get; } = new("focuser.main");
        public string Name => "Focuser";
        public DeviceType Type => DeviceType.Focuser;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Records what it observes while it runs, between other steps of a sequence.</summary>
    private sealed class ProbeStep(Action observe) : ISequenceStep
    {
        public string Name => "Probe";

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            observe();
            return Task.FromResult(new SequenceStepResult());
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static StartGuidingAction Start(SideraRuntimeHost host, string guiderId = "guider.main") =>
        new(host.DeviceRegistry, new DeviceId(guiderId));

    private static StopGuidingAction Stop(SideraRuntimeHost host, string guiderId = "guider.main") =>
        new(host.DeviceRegistry, new DeviceId(guiderId));

    private static FakeGuider AddFake(SideraRuntimeHost host, string id = "guider.main", bool block = false)
    {
        var guider = new FakeGuider(id) { Block = block };
        host.AddDevice(guider);
        return guider;
    }

    // The actions themselves

    [Fact]
    public async Task Actions_HaveNames_AndDeclareExactlyTheGuiderResource()
    {
        await using var host = new SideraRuntimeHost();

        Assert.Equal("Start guiding", Start(host).Name);
        Assert.Equal("Stop guiding", Stop(host).Name);
        Assert.Equal(new[] { new ResourceId("device:guider.main") }, Start(host).RequiredResources);
        Assert.Equal(new[] { new ResourceId("device:guider.main") }, Stop(host).RequiredResources);
        Assert.Throws<ArgumentNullException>(() => new StartGuidingAction(null!, GuiderId));
        Assert.Throws<ArgumentNullException>(() => new StopGuidingAction(null!, GuiderId));
    }

    [Fact]
    public async Task Actions_StartAndStop_AndReturnFreshResultsWithoutPayload()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host);
        var start = Start(host);

        var first = await start.ExecuteAsync(NoContext.Instance, default);
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        var second = await start.ExecuteAsync(NoContext.Instance, default);
        var stopped = await Stop(host).ExecuteAsync(NoContext.Instance, default);

        Assert.Null(first.Payload);
        Assert.Null(stopped.Payload);
        Assert.NotSame(first, second);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
        Assert.Equal(2, guider.StartCalls);
        Assert.Equal(1, guider.StopCalls);
    }

    [Fact]
    public async Task Actions_RejectUnknownAndNonGuiderDevices()
    {
        await using var host = new SideraRuntimeHost();
        host.AddDevice(new NotAGuider());

        var unknownStart = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Start(host).ExecuteAsync(NoContext.Instance, default));
        var unknownStop = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Stop(host).ExecuteAsync(NoContext.Instance, default));
        var wrongStart = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Start(host, "focuser.main").ExecuteAsync(NoContext.Instance, default));
        var wrongStop = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Stop(host, "focuser.main").ExecuteAsync(NoContext.Instance, default));

        Assert.Contains("'guider.main' is not registered", unknownStart.Message);
        Assert.Contains("'guider.main' is not registered", unknownStop.Message);
        Assert.Contains("'focuser.main' is not a guider", wrongStart.Message);
        Assert.Contains("'focuser.main' is not a guider", wrongStop.Message);
    }

    [Fact]
    public async Task Actions_RejectDisconnectedGuider_AndNeverConnectIt()
    {
        await using var host = new SideraRuntimeHost();
        var guider = new FakeGuider("guider.main", connected: false);
        host.AddDevice(guider);

        var start = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Start(host).ExecuteAsync(NoContext.Instance, default));
        var stop = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Stop(host).ExecuteAsync(NoContext.Instance, default));

        Assert.Contains("'guider.main' is not connected", start.Message);
        Assert.Contains("'guider.main' is not connected", stop.Message);
        Assert.Equal(0, guider.ConnectCalls);
        Assert.Equal(0, guider.StartCalls);
        Assert.Equal(0, guider.StopCalls);
        Assert.Equal(DeviceConnectionState.Disconnected, guider.ConnectionState);
    }

    [Fact]
    public async Task Actions_NeverConnectOrDisconnectTheSimulator()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Guider", Quick, Quick);
        var runner = new SequenceRunner(host.ResourceManager);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Start(host)])));
        Assert.Equal(DeviceConnectionState.Disconnected, guider.ConnectionState);

        await guider.ConnectAsync();
        await runner.RunAsync(new Sequence("s", [Start(host), Stop(host)]));
        Assert.Equal(DeviceConnectionState.Connected, guider.ConnectionState);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
    }

    // Sequences

    [Fact]
    public async Task Sequence_StartsGuiding_RunsAnotherStepWhileGuiding_ThenStops()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Guider", Quick, Quick);
        await guider.ConnectAsync();
        var runner = new SequenceRunner(host.ResourceManager);
        var completed = new List<string>();
        runner.StepCompleted += (_, e) => completed.Add(e.StepName);
        GuidingState? seen = null;
        bool? leaseHeld = null;
        var probe = new ProbeStep(() =>
        {
            seen = guider.GuidingState;
            leaseHeld = host.ResourceManager.IsHeld(GuiderResource);
        });

        await runner.RunAsync(new Sequence("s", [Start(host), new DelayAction(Quick), probe, Stop(host)]));

        Assert.Equal(GuidingState.Guiding, seen);
        Assert.False(leaseHeld); // the start released its lease while guiding stayed active
        Assert.Equal(new[] { "Start guiding", "Wait 0.01s", "Probe", "Stop guiding" }, completed);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
        Assert.True(host.StateStore.TryGet(GuiderId, out var state));
        Assert.Equal(GuidingState.Idle, state!.GuidingState);
    }

    [Fact]
    public async Task FailingStart_FailsTheRun_ReleasesTheLease_AndIsNotReportedCompleted()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host);
        guider.Failure = new InvalidOperationException("no guide star");
        var runner = new SequenceRunner(host.ResourceManager);
        var completed = new List<string>();
        runner.StepCompleted += (_, e) => completed.Add(e.StepName);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Start(host)])));

        Assert.Equal("no guide star", error.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Same(error, runner.Failure);
        Assert.Empty(completed);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }

    [Fact]
    public async Task CancelledStop_CancelsTheRun_ReleasesTheLease_AndIsNotReportedCompleted()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host, block: true);
        var runner = new SequenceRunner(host.ResourceManager);
        var completed = new List<string>();
        runner.StepCompleted += (_, e) => completed.Add(e.StepName);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [Stop(host)]), cts.Token);
        await guider.GateOf("stop", 1).Started.Task.WaitAsync(Bound);
        Assert.True(host.ResourceManager.IsHeld(GuiderResource));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Empty(completed);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }

    [Fact]
    public async Task SequenceCancelledWhileWaiting_NeverInvokesTheGuider()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host, block: true);
        var runner = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [Start(host), Stop(host)])]), cts.Token);
        await WaitUntil(() => guider.StartCalls + guider.StopCalls == 1, "one command reaching the guider");
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "the other command waiting");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Equal(1, guider.StartCalls + guider.StopCalls);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }

    [Fact]
    public async Task CommandsOnDifferentGuiders_RunConcurrently()
    {
        await using var host = new SideraRuntimeHost();
        var main = AddFake(host, "guider.main", block: true);
        var oag = AddFake(host, "guider.oag", block: true);
        var runner = new SequenceRunner(host.ResourceManager);

        var run = runner.RunAsync(new Sequence("s", [
            new ParallelStep("p", [Start(host, "guider.main"), Start(host, "guider.oag")]),
        ]));

        await Task.WhenAll(main.GateOf("start", 1).Started.Task, oag.GateOf("start", 1).Started.Task).WaitAsync(Bound);
        Assert.Equal(0, host.ResourceManager.WaitingCount);

        main.GateOf("start", 1).Release.SetResult();
        oag.GateOf("start", 1).Release.SetResult();
        await run.WaitAsync(Bound);
        Assert.Equal(GuidingState.Guiding, main.GuidingState);
        Assert.Equal(GuidingState.Guiding, oag.GuidingState);
    }

    // Manual operations and arbitration

    [Fact]
    public async Task ManualStartAndStop_ReleaseTheLease_WhileGuidingStaysActiveInBetween()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Guider", Quick, Quick);
        await guider.ConnectAsync();

        await host.DeviceOperations.StartGuidingAsync(GuiderId);
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));

        await host.DeviceOperations.StopGuidingAsync(GuiderId);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
    }

    [Fact]
    public async Task ManualOperations_FailClearly_ForUnknownOrNonGuiderDevices()
    {
        await using var host = new SideraRuntimeHost();
        host.AddDevice(new NotAGuider());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.DeviceOperations.StartGuidingAsync(new DeviceId("nope")));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.DeviceOperations.StopGuidingAsync(new DeviceId("focuser.main")));

        Assert.Contains("'nope' is not registered", unknown.Message);
        Assert.Contains("'focuser.main' is not a guider", wrong.Message);
    }

    [Fact]
    public async Task ManualStart_OnADisconnectedSimulator_FailsWithoutConnecting()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Guider", Quick, Quick);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.StartGuidingAsync(GuiderId));

        Assert.Equal(DeviceConnectionState.Disconnected, guider.ConnectionState);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
    }

    [Fact]
    public async Task ManualStart_BlocksSequenceStopOnTheSameGuider_UntilItReleases()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host, block: true);
        var runner = new SequenceRunner(host.ResourceManager);

        var manual = host.DeviceOperations.StartGuidingAsync(GuiderId);
        await guider.GateOf("start", 1).Started.Task.WaitAsync(Bound);
        var sequence = runner.RunAsync(new Sequence("s", [Stop(host)]));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "sequence stop waiting for the manual start");
        Assert.Equal(0, guider.StopCalls);

        guider.GateOf("start", 1).Release.SetResult();
        await manual.WaitAsync(Bound);
        await guider.GateOf("stop", 1).Started.Task.WaitAsync(Bound);
        guider.GateOf("stop", 1).Release.SetResult();
        await sequence.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
    }

    [Fact]
    public async Task SequenceStart_BlocksManualStopOnTheSameGuider_UntilItReleases()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host, block: true);
        var runner = new SequenceRunner(host.ResourceManager);

        var sequence = runner.RunAsync(new Sequence("s", [Start(host)]));
        await guider.GateOf("start", 1).Started.Task.WaitAsync(Bound);
        var manual = host.DeviceOperations.StopGuidingAsync(GuiderId);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "manual stop waiting for the sequence start");
        Assert.Equal(0, guider.StopCalls);
        Assert.False(manual.IsCompleted);

        guider.GateOf("start", 1).Release.SetResult();
        await sequence.WaitAsync(Bound);
        await guider.GateOf("stop", 1).Started.Task.WaitAsync(Bound);
        guider.GateOf("stop", 1).Release.SetResult();
        await manual.WaitAsync(Bound);

        Assert.Equal(GuidingState.Idle, guider.GuidingState);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
    }

    [Fact]
    public async Task ManualCancelledWhileWaiting_NeverInvokesTheGuider_AndLeavesTheQueueClean()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host, block: true);
        var first = host.DeviceOperations.StartGuidingAsync(GuiderId);
        await guider.GateOf("start", 1).Started.Task.WaitAsync(Bound);
        using var cts = new CancellationTokenSource();

        var second = host.DeviceOperations.StopGuidingAsync(GuiderId, cts.Token);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "the stop waiting");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Bound));
        Assert.Equal(0, guider.StopCalls);
        Assert.Equal(0, host.ResourceManager.WaitingCount);

        guider.GateOf("start", 1).Release.SetResult();
        await first.WaitAsync(Bound);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
    }

    [Fact]
    public async Task ManualFailureAndCancellation_ReleaseTheLease()
    {
        await using var host = new SideraRuntimeHost();
        var guider = AddFake(host, block: true);
        using var cts = new CancellationTokenSource();

        var start = host.DeviceOperations.StartGuidingAsync(GuiderId, cts.Token);
        await guider.GateOf("start", 1).Started.Task.WaitAsync(Bound);
        Assert.True(host.ResourceManager.IsHeld(GuiderResource));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Bound));
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));

        guider.Failure = new InvalidOperationException("guide camera lost");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.DeviceOperations.StopGuidingAsync(GuiderId));
        Assert.Equal("guide camera lost", error.Message);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }
}
