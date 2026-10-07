using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;
using Sidera.Runtime.Sequencing;
using StartGuidingAction = Sidera.Desktop.Sessions.StartGuidingAction;
using StopGuidingAction = Sidera.Desktop.Sessions.StopGuidingAction;
using AutofocusAction = Sidera.Desktop.Sessions.AutofocusAction;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>
/// A session compiled and run by the real runner on the simulator: what the automation does and when, that nothing deadlocks, and what is shared stays coordinated. Time is the real one, so the
/// intervals are short and the assertions about them are bounds, not exact moments.
/// </summary>
public sealed class SessionRunTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly FocusSettings QuickFocus = new(0.1, 400, 7);
    private static readonly DitherSettings QuickDither = new(0.6, 0.5, 0.1, 5);

    private static DemoOptions Options => new()
    {
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(150),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
        FocuserStepsPerSecond = 20000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(50),
    };

    private SideraRuntimeHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private async Task<SideraRuntimeHost> CreateAsync()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Options);
        DemoSetup.AddDemoRigs(host);
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        _host = host;
        return host;
    }

    private static SessionAction StartGuiding() => new StartGuidingAction(Guid.NewGuid());

    private static SessionAction StopGuiding() => new StopGuidingAction(Guid.NewGuid());

    /// <summary>The block of the tests: <paramref name="frames"/> frames of <paramref name="seconds"/> with the quick settings of the simulator wherever the automation has any.</summary>
    private static SequenceBlock Imaging(int frames, double seconds, int? slot = null, int? ditherEvery = null, FocusAutomation? focus = null) =>
        Block(slot, seconds, frames, b =>
        {
            if (ditherEvery is { } every)
            {
                b.Dither(every, QuickDither);
            }

            if (focus is not null)
            {
                b.Focus(focus.AtBlockStart, focus.EveryMinutes, focus.AfterFilterChange, QuickFocus);
            }
        });

    private static FocusAutomation Focus(bool atStart = false, double interval = 0, bool afterFilter = false) => new(atStart, interval, afterFilter, QuickFocus);

    private static SessionDefinition OneTarget(IReadOnlyList<SetupLane> lanes, IReadOnlyList<SessionAction>? preparation = null, IReadOnlyList<SessionAction>? end = null) =>
        Session(Target("M42", lanes, preparation)) with { End = end ?? [] };

    // What a run did, seen through every change of the runner.
    private sealed class Observation
    {
        private readonly object _gate = new();
        public Dictionary<string, int> Completed { get; } = [];
        public int MostExposuresAtOnce { get; private set; }
        public bool ExposedWhileDithering { get; private set; }

        public void Attach(SequenceRunner runner)
        {
            runner.Changed += (_, _) =>
            {
                var names = runner.ActivePositions.Select(p => p.StepName).ToList();
                var exposures = names.Count(n => n.StartsWith("Exposure", StringComparison.Ordinal));
                lock (_gate)
                {
                    MostExposuresAtOnce = Math.Max(MostExposuresAtOnce, exposures);
                    if (names.Any(n => n == "Dither command") && exposures > 0)
                    {
                        ExposedWhileDithering = true;
                    }
                }
            };
            runner.StepCompleted += (_, e) =>
            {
                lock (_gate)
                {
                    var name = e.StepName.StartsWith("Exposure", StringComparison.Ordinal) ? "Exposure" : (e.StepName.StartsWith("Dither ", StringComparison.Ordinal) && e.StepName.EndsWith(" px", StringComparison.Ordinal)) ? "Dither" : e.StepName;
                    Completed[name] = Completed.GetValueOrDefault(name) + 1;
                }
            };
        }

        public int Count(string name)
        {
            lock (_gate)
            {
                return Completed.GetValueOrDefault(name);
            }
        }
    }

    private static (SequenceRunner Runner, Sidera.Core.Sequencing.Sequence Sequence) Build(SideraRuntimeHost host, SessionDefinition session)
    {
        var compiled = SessionCompiler.Compile(session, new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry));
        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var focus = SequenceDraftDefaults.From(Options, host.DeviceRegistry);
        var context = new SequenceDraftContext(
            host.RigRegistry, SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), focus.MountId, focus.GuiderId, false), host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, compiled.Steps, context);
        return (new SequenceRunner(host.ResourceManager, host.SafePointCoordinator), built.Sequence);
    }

    private async Task<Observation> RunAsync(SideraRuntimeHost host, SessionDefinition session)
    {
        var (runner, sequence) = Build(host, session);
        var observation = new Observation();
        observation.Attach(runner);

        await runner.RunAsync(sequence).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        return observation;
    }

    [Fact]
    public async Task MainAndWide_ImageInParallel_WithOneGuider_AndACoordinatedDither_AndTheSequenceCompletes()
    {
        var host = await CreateAsync();
        var session = OneTarget([Lane(Main, Imaging(6, 0.2, ditherEvery: 3)), Lane(Wide, Imaging(12, 0.1))], [StartGuiding()], [StopGuiding()]);

        var run = await RunAsync(host, session);

        Assert.Equal(1, run.Count("Start guiding")); // the guider is shared: started once, stopped once
        Assert.Equal(1, run.Count("Stop guiding"));
        Assert.Equal(18, run.Count("Exposure"));
        Assert.Equal(2, run.Count("Dither")); // after frames 3 and 6 of Main, one dither each, for both setups
        Assert.Equal(2, run.MostExposuresAtOnce); // both cameras exposed at the same time
        Assert.False(run.ExposedWhileDithering); // the dither waited for both
    }

    [Fact]
    public async Task AnAutofocusInterval_FocusesBetweenExposures_NeverInsideOne_AndTheSequenceCompletes()
    {
        var host = await CreateAsync();
        var session = OneTarget([Lane(Main, Imaging(12, 0.25, focus: Focus(interval: 0.01)))]); // 0.6 s

        var run = await RunAsync(host, session);

        Assert.Equal(12, run.Count("Exposure"));
        Assert.InRange(run.Count("Autofocus"), 1, 6);
        Assert.Equal(1, run.MostExposuresAtOnce);
    }

    [Fact]
    public async Task ALongInterval_NeverFocuses()
    {
        var host = await CreateAsync();

        var run = await RunAsync(host, OneTarget([Lane(Main, Imaging(4, 0.1, focus: Focus(interval: 30)))]));

        Assert.Equal(0, run.Count("Autofocus"));
    }

    [Fact]
    public async Task AutofocusAtTheStart_AndAnInterval_AreOneAutofocus_WhenTheyFallTogether()
    {
        var host = await CreateAsync();

        var run = await RunAsync(host, OneTarget([Lane(Main, Imaging(4, 0.1, focus: Focus(atStart: true, interval: 30)))]));

        Assert.Equal(1, run.Count("Autofocus")); // at the start; the interval counts from there
    }

    [Fact]
    public async Task AFilterChange_AndAnIntervalThatIsDueAtTheSameTime_AreOneAutofocus()
    {
        var host = await CreateAsync();
        var session = OneTarget([Lane(Main, Imaging(3, 0.05, slot: 1, focus: Focus(interval: 0.005, afterFilter: true)))]); // 0.3 s; the three frames take less

        var run = await RunAsync(host, session);

        Assert.Equal(1, run.Count("Autofocus")); // the one after the filter change; it started the interval again
    }

    [Fact]
    public async Task TwoSetupsOnOneGuider_SharingADither_DoNotDitherTwice()
    {
        var host = await CreateAsync();
        var session = OneTarget([Lane(Main, Imaging(6, 0.1, ditherEvery: 3)), Lane(Wide, Imaging(6, 0.1))], [StartGuiding()], [StopGuiding()]);

        var run = await RunAsync(host, session);

        Assert.Equal(2, run.Count("Dither")); // 6 frames of the counted setup, every 3: not 2 for each setup
    }

    [Fact]
    public async Task AnExplicitAutofocusInThePreparation_StillRuns()
    {
        var host = await CreateAsync();
        var session = OneTarget([Lane(Main, Imaging(2, 0.1))], [new AutofocusAction(Guid.NewGuid(), QuickFocus) { Setup = Main }]);

        var run = await RunAsync(host, session);

        Assert.Equal(1, run.Count("Autofocus"));
    }

    [Fact]
    public async Task ASingleSetup_CanDither_WithoutAnyoneToWaitFor()
    {
        var host = await CreateAsync();
        var session = OneTarget([Lane(Main, Imaging(6, 0.1, ditherEvery: 2))], [StartGuiding()], [StopGuiding()]);

        var run = await RunAsync(host, session);

        Assert.Equal(6, run.Count("Exposure"));
        Assert.Equal(3, run.Count("Dither")); // after frames 2, 4 and 6
        Assert.False(run.ExposedWhileDithering);
    }

    [Fact]
    public async Task WhenADitherAndAnAutofocusAreDueTogether_TheDitherComesFirst_ThenTheAutofocus_AndEachOnlyOnce()
    {
        var host = await CreateAsync();
        // Dither after every frame; the interval is short enough to be due at the first boundary.
        var session = OneTarget([Lane(Main, Imaging(3, 0.7, ditherEvery: 1, focus: Focus(interval: 0.005)))], [StartGuiding()], [StopGuiding()]); // 0.3 s: due after the first 0.7 s frame
        var (runner, sequence) = Build(host, session);
        var order = new List<string>();
        runner.StepCompleted += (_, e) =>
        {
            lock (order)
            {
                if (e.StepName.StartsWith("Exposure", StringComparison.Ordinal))
                {
                    order.Add("E");
                }
                else if (e.StepName.StartsWith("Dither ", StringComparison.Ordinal) && e.StepName.EndsWith(" px", StringComparison.Ordinal))
                {
                    order.Add("D");
                }
                else if (e.StepName == "Autofocus")
                {
                    order.Add("A");
                }
            }
        };

        await runner.RunAsync(sequence).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        string text;
        lock (order)
        {
            text = string.Concat(order);
        }

        // Frame 1, then its dither (counted right after the exposure), then the autofocus that came due, then frame 2 ... The first boundary has both, in that order.
        Assert.StartsWith("EDA", text, StringComparison.Ordinal);
        Assert.Equal(3, text.Count(c => c == 'D'));
        Assert.True(text.Count(c => c == 'A') >= 1);
    }
}
