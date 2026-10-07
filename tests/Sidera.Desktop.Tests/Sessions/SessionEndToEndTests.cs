using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;
using Sidera.Core.Sequencing;
using Sidera.Desktop;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>
/// The representative session, through the editor and the runner on the simulator: M31 from the framing, its preparation (Slew &amp; Center, Autofocus, Start Guiding), Main and Wide imaging at the
/// same time on one guider with a dither and an autofocus interval, and Stop Guiding at the end; the block cards show the progress and the sequence completes.
/// </summary>
public sealed class SessionEndToEndTests : IAsyncLifetime
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
    public async Task TheRepresentativeSession_RunsToTheEnd_WithOneGuider_ADitherAndAnAutofocus_AndTheCardsShowProgress()
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
        var editor = _vm.SessionEditor;
        await _vm.SequenceDocument.NewCommand.ExecuteAsync(null);
        if (_vm.SequenceDocument.IsConfirmingDiscard)
        {
            await _vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        // The target from the framing, then the rest through the editor.
        Assert.NotNull(_vm.SequenceDraft.TargetSink!(new SessionTargetRequest("M31", 0.7123, 41.269, null, new Sidera.Core.Rigs.RigId("rig.main"))));
        editor.Targets[0].AddLaneCommand.Execute(null);
        var target = editor.Session!.Targets[0];
        Assert.Equal(2, target.Lanes.Count);
        Assert.Equal([SessionActionKind.SlewAndCenter, SessionActionKind.Autofocus, SessionActionKind.StartGuiding], target.Preparation.Select(a => a.Kind));
        Assert.Equal([SessionActionKind.StopGuiding], editor.Session.End.Select(a => a.Kind));

        editor.SelectBlock(target.Lanes[0].Blocks[0].Id);
        var main = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        main.ExposureText = "0.2";
        main.RepeatCountText = "10";
        main.DitherOn = true;
        main.DitherEveryText = "3";
        main.DitherThresholdText = "0.5";
        main.DitherStableText = "0.1";
        main.FocusOn = true;
        main.FocusAtStart = false;
        main.FocusEveryOn = true;
        main.FocusEveryText = "0.01";
        main.FocusExposureText = "0.1";

        editor.SelectBlock(target.Lanes[1].Blocks[0].Id);
        var wide = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        wide.ExposureText = "0.1";
        wide.RepeatCountText = "20";

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.True(_vm.SequenceDraft.IsValid, string.Join(" ", _vm.SequenceDraft.ValidationErrors));

        _vm.Sequencer.RunCommand.Execute(null);
        var sawProgress = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (_vm.Sequencer.State is SequenceState.Running or SequenceState.Idle or SequenceState.Pausing)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sequence.");
            sawProgress |= editor.Targets.SelectMany(t => t.Lanes).SelectMany(l => l.Blocks).Any(b => b.HasProgress && b.ProgressText.Contains('/', StringComparison.Ordinal));
            await Task.Delay(10);
        }

        Assert.Equal(SequenceState.Completed, _vm.Sequencer.State);
        Assert.True(sawProgress, "The block cards showed no frames while the sequence ran.");
    }
}
