using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Resources;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The guider card and page for a guider with measurements: what it shows, when its buttons work, and that a stream of samples costs little.</summary>
public sealed class GuiderViewModelTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    // A guider that measures, driven by the test.
    private sealed class MeasuringGuider : IDitherGuider, IGuidingSettler, IGuiderControl
    {
        private GuidingState _state = GuidingState.Idle;

        public DeviceId Id { get; } = new("guider.main");
        public string Name => "Test Guider";
        public DeviceType Type => DeviceType.Guider;
        public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;
        public GuidingState GuidingState => _state;
        public GuidingHistory History { get; } = new();
        public GuidingTelemetry? Telemetry { get; set; }
        public GuiderInfo? Info { get; set; }
        public bool CanPause { get; set; } = true;
        public int Stops { get; private set; }
        public int Pauses { get; private set; }
        public int Resumes { get; private set; }

        public DeviceCapabilities<GuiderCapabilities> Capabilities =>
            DeviceCapabilities<GuiderCapabilities>.Of(new GuiderCapabilities { CanDither = true, CanSettle = true, CanPause = CanPause, ProvidesGuideTelemetry = true });

        public event EventHandler? CapabilitiesChanged { add { } remove { } }
        public event EventHandler? StateChanged;

        public void Set(GuidingState state)
        {
            _state = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
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

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartGuidingAsync(CancellationToken cancellationToken = default)
        {
            Set(GuidingState.Guiding);
            return Task.CompletedTask;
        }

        public Task StopGuidingAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            Set(GuidingState.Idle);
            return Task.CompletedTask;
        }

        public Task PauseGuidingAsync(CancellationToken cancellationToken = default)
        {
            Pauses++;
            Set(GuidingState.Paused);
            return Task.CompletedTask;
        }

        public Task ResumeGuidingAsync(CancellationToken cancellationToken = default)
        {
            Resumes++;
            Set(GuidingState.Guiding);
            return Task.CompletedTask;
        }

        public Task DitherAsync(double amplitudePixels, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SettleAsync(GuidingSettleOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(GuiderViewModel Vm, MeasuringGuider Guider, SideraRuntimeHost Host, List<Action> Posted)> Create(bool connect = true)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var guider = new MeasuringGuider();
        host.AddDevice(guider);
        var posted = new List<Action>();
        var vm = new GuiderViewModel(guider, host, a => { lock (posted) { posted.Add(a); } a(); }, new SessionActivity());
        _disposables.Add(vm);
        if (connect)
        {
            await guider.ConnectAsync();
            vm.Refresh();
        }

        return (vm, guider, host, posted);
    }

    [Fact]
    public async Task AGuiderWithMeasurements_ShowsTheRms_TheStar_AndTheSetup_InWords()
    {
        var (vm, guider, _, _) = await Create();
        guider.Telemetry = new GuidingTelemetry
        {
            Rms = new GuidingRms(0.41, 0.47, 0.62, 100),
            StarSnr = 28.44,
            ExposureSeconds = 2,
            PixelScaleArcsecPerPixel = 3.8,
        };
        guider.Info = new GuiderInfo("Main Rig", "ASI120MM Mini", "AM3", true, true);

        guider.Raise();
        await Eventually(() => vm.SetupLines.Count == 5); // the last of what one update sets: the update is complete

        Assert.Equal(("0.41\"", "0.47\"", "28.4", "2 s"), (vm.RmsRaText, vm.RmsDecText, vm.SnrText, vm.ExposureText));
        Assert.Equal("3.8 \"/px", vm.PixelScaleText);
        Assert.True(vm.GraphInArcseconds);
        Assert.Equal(["Profile", "Guide camera", "Mount", "Calibration", "PHD2 equipment"], vm.SetupLines.Select(l => l.Label));
        Assert.Equal("Calibrated", vm.SetupLines.Single(l => l.Label == "Calibration").Value);
    }

    [Fact]
    public async Task WhatIsNotKnown_IsADash_NeverAZero_AndTheGraphFallsBackToPixels()
    {
        var (vm, guider, _, _) = await Create();
        guider.Telemetry = new GuidingTelemetry { PixelScaleArcsecPerPixel = null };

        guider.Raise();
        await Eventually(() => !vm.GraphInArcseconds);

        Assert.Equal(("—", "—", "—", "—"), (vm.RmsText, vm.RmsRaText, vm.SnrText, vm.ExposureText));
    }

    [Fact]
    public async Task TheSettle_IsShown_WhileItRuns_AndAfterItEnded()
    {
        var (vm, guider, _, _) = await Create();
        guider.Set(GuidingState.Settling);
        guider.Telemetry = new GuidingTelemetry
        {
            Settle = new GuidingSettleStatus(true, 0.8, TimeSpan.FromSeconds(2), 1.5, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(3), GuidingSettleOutcome.None, null, null),
        };
        guider.Raise();
        await Eventually(() => vm.SettleTimeoutText == "40 s"); // the last of what one update sets for the settle (the test reads from another thread)
        Assert.Equal(("Settling · 0.80 px", "1.5 px", "8 s", "40 s"), (vm.SettleStateText, vm.SettleToleranceText, vm.SettleStableText, vm.SettleTimeoutText));

        guider.Set(GuidingState.Guiding);
        vm.Refresh(); // the guider says so with an event on the bus of the host; the test has none
        guider.Telemetry = new GuidingTelemetry
        {
            Settle = new GuidingSettleStatus(false, null, null, 1.5, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(5), GuidingSettleOutcome.Settled, TimeSpan.FromSeconds(1.8), null),
        };
        guider.Raise();
        await Eventually(() => vm.LastSettleText == "Settled in 1.8 s");
        Assert.Equal("Stable", vm.SettleStateText);

        guider.Telemetry = new GuidingTelemetry
        {
            Settle = new GuidingSettleStatus(false, null, null, 1.5, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), GuidingSettleOutcome.TimedOut, TimeSpan.FromSeconds(40), "Settling timed out"),
        };
        guider.Raise();
        await Eventually(() => vm.LastSettleText.StartsWith("Timed out"));
        Assert.Contains("Settling timed out", vm.LastSettleText);
    }

    [Theory]
    [InlineData(GuidingState.Idle, true, false, false, false)]
    [InlineData(GuidingState.Looping, true, true, false, false)]
    [InlineData(GuidingState.Guiding, false, true, true, false)]
    [InlineData(GuidingState.Paused, false, true, false, true)]
    [InlineData(GuidingState.Calibrating, false, true, false, false)]
    [InlineData(GuidingState.Settling, false, true, false, false)]
    [InlineData(GuidingState.StarLost, false, true, false, false)]
    public async Task TheButtons_FollowTheState(GuidingState state, bool start, bool stop, bool pause, bool resume)
    {
        var (vm, guider, _, _) = await Create();

        guider.Set(state);
        vm.Refresh();

        Assert.Equal((start, stop, pause, resume),
            (vm.StartGuidingCommand.CanExecute(null), vm.StopGuidingCommand.CanExecute(null), vm.PauseCommand.CanExecute(null), vm.ResumeCommand.CanExecute(null)));
    }

    [Fact]
    public async Task APauseThatTheGuiderCannotDo_IsNotOffered()
    {
        var (vm, guider, _, _) = await Create();
        guider.CanPause = false;
        guider.Set(GuidingState.Guiding);
        vm.Refresh();

        Assert.False(vm.SupportsPause);
        Assert.False(vm.PauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task PausingAndResuming_GoToTheGuider()
    {
        var (vm, guider, _, _) = await Create();
        guider.Set(GuidingState.Guiding);
        vm.Refresh();

        await vm.PauseCommand.ExecuteAsync(null);
        await vm.ResumeCommand.ExecuteAsync(null);

        Assert.Equal((1, 1), (guider.Pauses, guider.Resumes));
    }

    [Fact]
    public async Task Stop_WhileGuidingIsStarting_IsNotHeldUpByTheLeaseOfTheStart()
    {
        var (vm, guider, host, _) = await Create();
        guider.Set(GuidingState.Starting);
        vm.Refresh();
        using var lease = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(guider.Id)]); // what a start that is under way holds

        await vm.StopGuidingCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, guider.Stops);
    }

    [Fact]
    public async Task ADisconnect_IsNotOffered_WhileTheGuiderCalibratesOrSettles()
    {
        var (vm, guider, _, _) = await Create();

        guider.Set(GuidingState.Calibrating);
        vm.Refresh();
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        guider.Set(GuidingState.Guiding);
        vm.Refresh();
        Assert.True(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStreamOfSamples_CostsAFewUpdatesOfTheUi_NotOneForEach_AndTheGraphGetsTheVersion()
    {
        var (vm, guider, _, posted) = await Create();
        guider.Telemetry = new GuidingTelemetry { Rms = new GuidingRms(0.4, 0.4, 0.57, 10) };
        int before;
        lock (posted)
        {
            before = posted.Count;
        }

        for (var i = 0; i < 5000; i++)
        {
            guider.History.Add(new GuidingSample(DateTimeOffset.UtcNow, 0.1, 0.1, 0.2, 0.2));
        }

        await Task.Delay(400);
        int updates;
        lock (posted)
        {
            updates = posted.Count - before;
        }

        Assert.InRange(updates, 1, 20); // 5000 samples in a few milliseconds are a handful of updates
        Assert.Equal(guider.History.Version, vm.GraphVersion);
    }

    [Fact]
    public async Task ClearGraph_EmptiesTheHistory()
    {
        var (vm, guider, _, _) = await Create();
        guider.History.Add(new GuidingSample(DateTimeOffset.UtcNow, 0.1, 0.1, 0.2, 0.2));

        vm.ClearGraphCommand.Execute(null);

        Assert.Equal(0, guider.History.Count);
    }

    [Fact]
    public async Task TheGraphWindow_OffersSixtyOneTwentyAndThreeHundredSeconds_AndStartsAtTwoMinutes()
    {
        var (vm, _, _, _) = await Create();

        Assert.Equal([60.0, 120.0, 300.0], vm.GraphWindows);
        Assert.Equal(120, vm.GraphWindowSeconds);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(10);
        }
    }
}
