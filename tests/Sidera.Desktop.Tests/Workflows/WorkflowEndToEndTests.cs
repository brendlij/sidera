using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;
using Sidera.Core.Sequencing;
using Sidera.Desktop;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>
/// The representative workflow of the Sequencer UX slice, through the editor and the runner on the simulator: M31, Slew &amp; Center, Autofocus and Start Guiding to prepare, Main and Wide imaging
/// at the same time on one guider with a dither, an autofocus interval, and Stop Guiding to finish; the table shows the progress and the sequence completes.
/// </summary>
public sealed class WorkflowEndToEndTests : IAsyncLifetime
{
    private sealed class Solver(SideraRuntimeHost host) : IPlateSolver
    {
        public string Name => "Simulated";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = 0, Backend = Name });
        }
    }

    private SideraRuntimeHost? _host;
    private MainViewModel? _vm;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _vm?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheRepresentativeWorkflow_RunsToTheEnd_WithOneGuider_ADitherAndAnAutofocus_AndTheTableShowsProgress()
    {
        var options = new DemoOptions
        {
            SlewDuration = TimeSpan.FromMilliseconds(20), GuiderStartDuration = TimeSpan.FromMilliseconds(20), GuiderStopDuration = TimeSpan.FromMilliseconds(20),
            GuiderDitherDuration = TimeSpan.FromMilliseconds(150), DitherAmplitudePixels = 0.6, SettleStableDuration = TimeSpan.FromMilliseconds(100), SettleTimeout = TimeSpan.FromSeconds(5),
            FocuserStepsPerSecond = 20000, FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20), FilterWheelMoveDuration = TimeSpan.FromMilliseconds(50),
        };
        _host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(_host, options);
        DemoSetup.AddDemoRigs(_host);
        _host.ConfigurePlateSolver(new Solver(_host));
        foreach (var device in _host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        _vm = new MainViewModel(_host, a => a(), options);
        var editor = _vm.Workflow;
        await _vm.SequenceDocument.NewCommand.ExecuteAsync(null);
        if (_vm.SequenceDocument.IsConfirmingDiscard)
        {
            await _vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        // The target from the framing, then the rest through the editor.
        Assert.NotNull(_vm.SequenceDraft.TargetSink!(new WorkflowTargetRequest("M31", 0.7123, 41.269, null, new Sidera.Core.Rigs.RigId("rig.main"))));
        editor.AddAutofocusStepCommand.Execute(null);
        editor.AddStartGuidingCommand.Execute(null);
        editor.AddStopGuidingCommand.Execute(null);
        editor.AddImagingBlockCommand.Execute(null);
        Assert.Equal(2, editor.ImagingRows.Count);
        var main = editor.ImagingRows.Single(r => r.SetupLabel == "Main Rig");
        var wide = editor.ImagingRows.Single(r => r.SetupLabel != "Main Rig");
        main.SelectedSetup = main.SetupChoices.Single(c => c.Name == "Main Rig");
        wide.SelectedSetup = wide.SetupChoices.Single(c => c.Name == "Wide Rig");
        main.ExposureText = "0.2";
        main.FramesInputText = "10";
        wide.ExposureText = "0.1";
        wide.FramesInputText = "20";
        editor.DitherEnabled = true;
        editor.DitherEveryText = "3";
        editor.DitherThresholdText = "0.5";
        editor.DitherStableText = "0.1";
        editor.SelectRow(main);
        editor.PolicyEnabled = true;
        editor.PolicyIntervalText = "0.01";
        editor.PolicyExposureText = "0.1";

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.True(_vm.SequenceDraft.IsValid, string.Join(" ", _vm.SequenceDraft.ValidationErrors));

        _vm.Sequencer.RunCommand.Execute(null);
        var sawProgress = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (_vm.Sequencer.State is SequenceState.Running or SequenceState.Idle or SequenceState.Pausing)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sequence.");
            sawProgress |= editor.ImagingRows.Any(r => r.HasProgress && r.ProgressText.Contains('/', StringComparison.Ordinal));
            await Task.Delay(10);
        }

        Assert.Equal(SequenceState.Completed, _vm.Sequencer.State);
        Assert.True(sawProgress, "The imaging rows showed no frames while the sequence ran.");
        Assert.False(editor.ImagingRows[0].HasProgress && _vm.Sequencer.IsRunning);
    }
}
