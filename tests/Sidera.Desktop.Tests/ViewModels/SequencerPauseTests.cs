using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

public class SequencerPauseTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    /// <summary>A step that signals when it starts and ends when the test releases it, or fails when told to.</summary>
    private sealed class GateStep(string name) : ISequenceStep
    {
        public string Name { get; } = name;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? FailWhenReleased { get; init; }

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (FailWhenReleased is not null)
            {
                throw FailWhenReleased;
            }

            return new SequenceStepResult();
        }
    }

    private sealed class WorkStep(string name) : ISequenceStep
    {
        private int _executions;
        public string Name { get; } = name;
        public int Executions => _executions;

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new SequenceStepResult());
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required SequencerViewModel Sequencer { get; init; }
        public required SessionActivity Activity { get; init; }
        public required ButtonStates Buttons { get; init; }

        public async ValueTask DisposeAsync()
        {
            Sequencer.Dispose();
            await Host.DisposeAsync();
        }
    }

    private static Setup Create(Sequence sequence)
    {
        var host = new SideraRuntimeHost();
        var activity = new SessionActivity();
        var sequencer = new SequencerViewModel(host, action => action(), activity, new ImagingViewModel(), [], sequence);
        return new Setup { Host = host, Sequencer = sequencer, Activity = activity, Buttons = new ButtonStates(sequencer) };
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

    /// <summary>
    /// What the four buttons show. Like a real button it reads CanExecute once at the start and again only when the
    /// command raises CanExecuteChanged, so a command that is not notified keeps showing its old state here too.
    /// </summary>
    private sealed class ButtonStates
    {
        private readonly Probe _run;
        private readonly Probe _pause;
        private readonly Probe _resume;
        private readonly Probe _cancel;

        public ButtonStates(SequencerViewModel vm)
        {
            _run = new Probe(vm.RunCommand);
            _pause = new Probe(vm.PauseCommand);
            _resume = new Probe(vm.ResumeCommand);
            _cancel = new Probe(vm.CancelCommand);
        }

        // Pause, Resume and Cancel must be right at once. Run is an async command: once a run has ended, the toolkit
        // announces that it may execute again a moment after the awaited task completes, so that one is awaited.
        public async Task ExpectAsync(bool run, bool pause, bool resume, bool cancel)
        {
            var deadline = DateTime.UtcNow + Bound;
            while (run && !_run.Enabled && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            Assert.Equal((run, pause, resume, cancel), (_run.Enabled, _pause.Enabled, _resume.Enabled, _cancel.Enabled));
        }
    }

    private sealed class Probe
    {
        public Probe(System.Windows.Input.ICommand command)
        {
            Enabled = command.CanExecute(null);
            command.CanExecuteChanged += (_, _) => Enabled = command.CanExecute(null);
        }

        public bool Enabled { get; private set; }
    }

    [Fact]
    public async Task StartingASequence_EnablesPauseAndCancel_AsTheButtonsSeeIt()
    {
        // The bug: the buttons were evaluated before the run counted as running and Pause was never notified again.
        var gate = new GateStep("exposure");
        await using var setup = Create(new Sequence("s", [gate, new WorkStep("after")]));
        await setup.Buttons.ExpectAsync(run: true, pause: false, resume: false, cancel: false);

        var run = setup.Sequencer.RunCommand.ExecuteAsync(null);
        await gate.Started.Task.WaitAsync(Bound);

        Assert.Equal(SequenceState.Running, setup.Sequencer.State);
        await setup.Buttons.ExpectAsync(run: false, pause: true, resume: false, cancel: true);

        setup.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task Buttons_FollowEveryTransition_Running_Pausing_Paused_Running_Paused_Cancelled()
    {
        var first = new GateStep("first");
        var second = new GateStep("second");
        await using var setup = Create(new Sequence("s", [first, second, new WorkStep("third")]));
        var vm = setup.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);                       // Idle -> Running
        await first.Started.Task.WaitAsync(Bound);
        await setup.Buttons.ExpectAsync(run: false, pause: true, resume: false, cancel: true);

        vm.PauseCommand.Execute(null);                                    // Running -> Pausing
        Assert.Equal(SequenceState.Pausing, vm.State);
        await setup.Buttons.ExpectAsync(run: false, pause: false, resume: false, cancel: true);

        first.Release.SetResult();                                        // Pausing -> Paused
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        await setup.Buttons.ExpectAsync(run: false, pause: false, resume: true, cancel: true);

        vm.ResumeCommand.Execute(null);                                   // Paused -> Running
        await second.Started.Task.WaitAsync(Bound);
        await WaitUntil(() => vm.State == SequenceState.Running, "running again");
        await setup.Buttons.ExpectAsync(run: false, pause: true, resume: false, cancel: true);

        vm.PauseCommand.Execute(null);
        second.Release.SetResult();
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused again");
        await setup.Buttons.ExpectAsync(run: false, pause: false, resume: true, cancel: true);

        vm.CancelCommand.Execute(null);                                   // Paused -> Cancelled
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Cancelled, vm.State);
        await setup.Buttons.ExpectAsync(run: true, pause: false, resume: false, cancel: false);
    }

    [Fact]
    public async Task Idle_OnlyRunIsAvailable()
    {
        await using var setup = Create(new Sequence("s", [new WorkStep("a")]));

        Assert.Equal(SequenceState.Idle, setup.Sequencer.State);
        await setup.Buttons.ExpectAsync(run: true, pause: false, resume: false, cancel: false);
    }

    [Fact]
    public async Task Running_OffersPauseAndCancel_Pausing_OffersOnlyCancel_Paused_OffersResumeAndCancel()
    {
        var first = new GateStep("first");
        var second = new WorkStep("second");
        await using var setup = Create(new Sequence("s", [first, second]));
        var vm = setup.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await first.Started.Task.WaitAsync(Bound);
        Assert.Equal("Running", vm.StateText);
        Assert.True(vm.IsRunningState);
        await setup.Buttons.ExpectAsync(run: false, pause: true, resume: false, cancel: true);

        vm.PauseCommand.Execute(null);
        Assert.Equal(SequenceState.Pausing, vm.State);
        Assert.Equal("Pausing…", vm.StateText);
        Assert.True(vm.IsPausing);
        Assert.True(vm.IsPauseState);
        Assert.False(vm.IsRunningState);
        await setup.Buttons.ExpectAsync(run: false, pause: false, resume: false, cancel: true);

        first.Release.SetResult();
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal("Paused", vm.StateText);
        Assert.True(vm.IsPaused);
        Assert.Equal(0, second.Executions);
        await setup.Buttons.ExpectAsync(run: false, pause: false, resume: true, cancel: true);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(1, second.Executions);
        await setup.Buttons.ExpectAsync(run: true, pause: false, resume: false, cancel: false);
    }

    [Fact]
    public async Task WhilePaused_TheWaitingStepIsShown_NotAContainerAsIfItWereRunning()
    {
        var first = new GateStep("first");
        var next = new WorkStep("next step");
        await using var setup = Create(new Sequence("s", [new SequenceGroup("block", [first, next])]));
        var vm = setup.Sequencer;
        var run = vm.RunCommand.ExecuteAsync(null);
        await first.Started.Task.WaitAsync(Bound);
        vm.PauseCommand.Execute(null);
        first.Release.SetResult();
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");

        var waiting = Assert.Single(vm.ActiveBranches);
        Assert.True(waiting.IsWaiting);
        Assert.Equal("Waiting to resume · next: next step", waiting.DisplayTitle);
        Assert.False(waiting.HasProgress);

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task ParallelBranches_AreShownWhilePausing_AndAsWaitingOncePaused()
    {
        var a1 = new GateStep("a1");
        var b1 = new GateStep("b1");
        var a2 = new WorkStep("a2");
        var b2 = new WorkStep("b2");
        await using var setup = Create(new Sequence("s", [
            new ParallelStep("p", [new SequenceGroup("MAIN", [a1, a2]), new SequenceGroup("WIDE", [b1, b2])]),
        ]));
        var vm = setup.Sequencer;
        var run = vm.RunCommand.ExecuteAsync(null);
        await Task.WhenAll(a1.Started.Task, b1.Started.Task).WaitAsync(Bound);

        vm.PauseCommand.Execute(null);
        Assert.Equal(new[] { "a1", "b1" }, vm.ActiveBranches.Select(b => b.Title).Order()); // both still run
        Assert.All(vm.ActiveBranches, b => Assert.False(b.IsWaiting));

        b1.Release.SetResult();
        await WaitUntil(() => vm.ActiveBranches.Any(b => b.IsWaiting), "WIDE waiting");
        Assert.Equal(SequenceState.Pausing, vm.State);   // MAIN still works on its step
        Assert.Contains(vm.ActiveBranches, b => b.Title == "a1" && !b.IsWaiting);

        a1.Release.SetResult();
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.All(vm.ActiveBranches, b => Assert.True(b.IsWaiting));
        Assert.Equal(new[] { "MAIN", "WIDE" }, vm.ActiveBranches.Select(b => b.BranchName).Order());
        Assert.Equal(0, a2.Executions + b2.Executions);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, vm.State);
    }

    [Fact]
    public async Task Cancel_WhilePaused_EndsTheRunWithoutAResume_AndTheNextRunIsNotPaused()
    {
        var first = new GateStep("first");
        var second = new WorkStep("second");
        await using var setup = Create(new Sequence("s", [first, second]));
        var vm = setup.Sequencer;
        var run = vm.RunCommand.ExecuteAsync(null);
        await first.Started.Task.WaitAsync(Bound);
        vm.PauseCommand.Execute(null);
        first.Release.SetResult();
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.True(vm.IsCancelled);
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.ActiveBranches);
        Assert.False(setup.Activity.IsSequenceRunning);
        await setup.Buttons.ExpectAsync(run: true, pause: false, resume: false, cancel: false);

        // A fresh run afterwards runs through; no stale pause remains.
        var again = new GateStep("first");
        await using var other = Create(new Sequence("s2", [again, new WorkStep("after")]));
        var run2 = other.Sequencer.RunCommand.ExecuteAsync(null);
        await again.Started.Task.WaitAsync(Bound);
        again.Release.SetResult();
        await run2.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, other.Sequencer.State);
    }

    [Fact]
    public async Task SameSequencer_CanRunAgainAfterACancelledPausedRun_WithoutBeingPaused()
    {
        var gate = new GateStep("first");
        var tail = new WorkStep("tail");
        await using var setup = Create(new Sequence("s", [gate, tail]));
        var vm = setup.Sequencer;
        var run = vm.RunCommand.ExecuteAsync(null);
        await gate.Started.Task.WaitAsync(Bound);
        vm.PauseCommand.Execute(null);
        gate.Release.SetResult();
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        // The gate is already released, so the second run passes it at once; only a leftover pause could stop it.
        await vm.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(1, tail.Executions);
    }

    [Fact]
    public async Task Cancel_WhilePausing_EndsTheRun()
    {
        var first = new GateStep("first");
        await using var setup = Create(new Sequence("s", [first, new WorkStep("second")]));
        var vm = setup.Sequencer;
        var run = vm.RunCommand.ExecuteAsync(null);
        await first.Started.Task.WaitAsync(Bound);
        vm.PauseCommand.Execute(null);
        Assert.Equal(SequenceState.Pausing, vm.State);

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task AFailureWhilePausing_ShowsFailed_WithAMessage_NotPaused()
    {
        var first = new GateStep("first") { FailWhenReleased = new InvalidOperationException("The camera lost its connection.") };
        await using var setup = Create(new Sequence("s", [first, new WorkStep("second")]));
        var vm = setup.Sequencer;
        var run = vm.RunCommand.ExecuteAsync(null);
        await first.Started.Task.WaitAsync(Bound);
        vm.PauseCommand.Execute(null);

        first.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Failed, vm.State);
        Assert.True(vm.IsFailed);
        Assert.StartsWith("The camera lost its connection. See the log, execution ", vm.ErrorMessage);
        await setup.Buttons.ExpectAsync(run: true, pause: false, resume: false, cancel: false);
    }

    [Fact]
    public async Task ManualControls_StayUnavailableWhilePaused_AndReturnAfterTheRunEnds()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var activity = new SessionActivity();
        var cameraVm = new CameraViewModel(camera, host, action => action(), activity, new ImagingViewModel(), TimeSpan.FromMilliseconds(30));
        await cameraVm.ConnectCommand.ExecuteAsync(null);
        var first = new GateStep("first");
        using var sequencer = new SequencerViewModel(
            host, action => action(), activity, new ImagingViewModel(), [cameraVm],
            new Sequence("s", [first, new WorkStep("second")]));
        Assert.True(cameraVm.StartExposureCommand.CanExecute(null));

        var run = sequencer.RunCommand.ExecuteAsync(null);
        await first.Started.Task.WaitAsync(Bound);
        sequencer.PauseCommand.Execute(null);
        first.Release.SetResult();
        await WaitUntil(() => sequencer.State == SequenceState.Paused, "paused");

        // A paused sequence still belongs to the user's session; a manual exposure would compete with it.
        Assert.False(cameraVm.StartExposureCommand.CanExecute(null));
        Assert.False(cameraVm.DisconnectCommand.CanExecute(null));

        sequencer.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.True(cameraVm.StartExposureCommand.CanExecute(null));
    }
}
