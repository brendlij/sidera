using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Guiding;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Guiding;

public class GuidingSettleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Interval = SimulatedGuider.SampleInterval;
    private static readonly DeviceId GuiderId = new("guider.main");

    private sealed class Recorder : IEventPublisher
    {
        private readonly List<ISideraEvent> _events = new();

        public ISideraEvent[] Events
        {
            get { lock (_events) { return _events.ToArray(); } }
        }

        public Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default)
            where TEvent : ISideraEvent
        {
            lock (_events)
            {
                _events.Add(sideraEvent);
            }

            return Task.CompletedTask;
        }
    }

    private static GuidingSettleOptions Options(double pixels = 0.5, int stableMs = 1000, int timeoutMs = 5000) =>
        new(pixels, TimeSpan.FromMilliseconds(stableMs), TimeSpan.FromMilliseconds(timeoutMs));

    // Options

    [Fact]
    public void Options_KeepTheirValues()
    {
        var options = new GuidingSettleOptions(0.75, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));

        Assert.Equal(0.75, options.MaximumErrorPixels);
        Assert.Equal(TimeSpan.FromSeconds(3), options.StableDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Options_RejectAThresholdThatIsNotFiniteAndPositive(double pixels)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GuidingSettleOptions(pixels, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)));
        Assert.Equal("maximumErrorPixels", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Options_RejectAStableDurationThatIsNotPositive(int stableMs)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GuidingSettleOptions(0.5, TimeSpan.FromMilliseconds(stableMs), TimeSpan.FromSeconds(10)));
        Assert.Equal("stableDuration", error.ParamName);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(999)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RequireATimeoutLongerThanTheStableDuration(int timeoutMs)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GuidingSettleOptions(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(timeoutMs)));
        Assert.Equal("timeout", error.ParamName);
    }

    // The settle criterion

    private static readonly TimeSpan Start = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMilliseconds(200);

    private static TimeSpan At(int ms) => Start + TimeSpan.FromMilliseconds(ms);

    private static GuideErrorObservation Seen(int ms, double pixels) => new(At(ms), pixels);

    private static GuidingSettleTracker Tracker(GuidingSettleOptions? options = null) =>
        new(options ?? Options(), Start, MaximumAge);

    [Fact]
    public void Criterion_IsMetOnlyAfterTheFullContinuousStableDuration()
    {
        var tracker = Tracker();

        for (var ms = 0; ms < 1000; ms += 100)
        {
            Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(ms), Seen(ms, 0.5)));
        }

        Assert.Equal(GuidingSettleProgress.Settled, tracker.Evaluate(At(1000), Seen(1000, 0.2)));
    }

    [Fact]
    public void Criterion_AnObservationAboveTheThreshold_RestartsTheInterval()
    {
        var tracker = Tracker();

        for (var ms = 0; ms <= 800; ms += 100)
        {
            tracker.Evaluate(At(ms), Seen(ms, 0.3));
        }

        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(900), Seen(900, 0.51)));
        for (var ms = 1000; ms < 2000; ms += 100)
        {
            Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(ms), Seen(ms, 0.3)));
        }

        Assert.Equal(GuidingSettleProgress.Settled, tracker.Evaluate(At(2000), Seen(2000, 0.3)));
    }

    public static TheoryData<string> UnusableObservations => new()
    {
        "missing", "stale", "NaN", "infinite", "negative", "from the future", "older than the previous one",
    };

    [Theory]
    [MemberData(nameof(UnusableObservations))]
    public void Criterion_AnUnusableObservation_NeitherEstablishesNorContinuesTheInterval(string kind)
    {
        var tracker = Tracker();
        GuideErrorObservation? Unusable(int ms) => kind switch
        {
            "missing" => null,
            "stale" => Seen(ms - 300, 0.1),
            "NaN" => Seen(ms, double.NaN),
            "infinite" => Seen(ms, double.PositiveInfinity),
            "negative" => Seen(ms, -0.1),
            "from the future" => Seen(ms + 50, 0.1),
            "older than the previous one" => Seen(ms - 150, 0.1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Only ever unusable: nothing is established, however long it goes on. (An observation can only be older
        // than the previous one once there was a previous one.)
        if (kind != "older than the previous one")
        {
            for (var ms = 400; ms <= 2000; ms += 100)
            {
                Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(ms), Unusable(ms)));
            }
        }

        // Interrupting a stable interval: the interval starts again after it.
        tracker = Tracker();
        for (var ms = 0; ms <= 900; ms += 100)
        {
            Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(ms), Seen(ms, 0.1)));
        }

        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(950), Unusable(950)));
        for (var ms = 1000; ms < 2000; ms += 100)
        {
            Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(ms), Seen(ms, 0.1)));
        }

        Assert.Equal(GuidingSettleProgress.Settled, tracker.Evaluate(At(2000), Seen(2000, 0.1)));
    }

    [Fact]
    public void Criterion_IgnoresObservationsFromBeforeTheStart_AndRepeatedObservationsDoNotAdvanceIt()
    {
        var tracker = Tracker();

        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(0), Seen(-100, 0.1)));
        // The same observation over and over is fresh for a while but proves no stability beyond its own time.
        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(0), Seen(0, 0.1)));
        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(200), Seen(0, 0.1)));
        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(300), Seen(0, 0.1)));
    }

    [Fact]
    public void Criterion_AGapLongerThanTheMaximumAge_RestartsTheInterval()
    {
        var tracker = Tracker();

        tracker.Evaluate(At(0), Seen(0, 0.1));
        // Fresh when evaluated, but nothing was seen for 900 ms before it.
        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(900), Seen(900, 0.1)));
        Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(1800), Seen(1800, 0.1)));
    }

    [Fact]
    public void Criterion_TimesOutFromTheStart_WhenStabilityIsNeverReached()
    {
        var tracker = Tracker(Options(timeoutMs: 3000));

        for (var ms = 0; ms < 3000; ms += 100)
        {
            Assert.Equal(GuidingSettleProgress.Settling, tracker.Evaluate(At(ms), Seen(ms, 0.8)));
        }

        Assert.Equal(GuidingSettleProgress.TimedOut, tracker.Evaluate(At(3000), Seen(3000, 0.8)));
        Assert.Equal(GuidingSettleProgress.TimedOut, tracker.Evaluate(At(3000), null));
    }

    // The simulated guider

    private static async Task<(SimulatedGuider Guider, ManualClock Clock, Recorder Events)> CreateGuiding()
    {
        var clock = new ManualClock();
        var events = new Recorder();
        var guider = new SimulatedGuider(
            GuiderId, clock, events: events, startDuration: Quick, stopDuration: Quick, ditherDuration: Quick);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        return (guider, clock, events);
    }

    /// <summary>Lets the settle loop take <paramref name="samples"/> samples without finishing, one interval apart.</summary>
    private static async Task AssertStillSettling(Task settle, ManualClock clock, int samples)
    {
        for (var i = 0; i < samples; i++)
        {
            await clock.NextDelay();
            Assert.False(settle.IsCompleted, $"Settle ended after {i} samples.");
            clock.Advance(Interval);
        }
    }

    [Fact]
    public async Task Simulator_IsASettler_ThatRequiresAConnectedGuidingGuider()
    {
        var clock = new ManualClock();
        var guider = new SimulatedGuider(GuiderId, clock, startDuration: Quick, stopDuration: Quick);

        Assert.IsAssignableFrom<IGuidingSettler>(guider);
        Assert.Null(guider.ObserveGuideError(clock.Now));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.SettleAsync(Options()));
        Assert.Contains("not connected", error.Message);

        await guider.ConnectAsync();
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => guider.SettleAsync(Options()));
        Assert.Contains("not guiding", error.Message);
        await Assert.ThrowsAsync<ArgumentNullException>(() => guider.SettleAsync(null!));
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
    }

    [Fact]
    public async Task Simulator_SteadyGuiding_SettlesAfterExactlyTheStableDuration()
    {
        var (guider, clock, events) = await CreateGuiding();
        var before = events.Events.Length;

        var settle = guider.SettleAsync(Options(stableMs: 1000));
        await AssertStillSettling(settle, clock, 10);
        await settle.WaitAsync(Bound);

        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        Assert.Equal(before, events.Events.Length);
    }

    [Fact]
    public async Task Simulator_DitherDisturbance_DecaysTowardsNormalGuidingError()
    {
        var (guider, clock, _) = await CreateGuiding();
        Assert.Equal(SimulatedGuider.NormalGuideErrorPixels, guider.ObserveGuideError(clock.Now)!.Value.ErrorPixels);

        await guider.DitherAsync(3);

        var peak = guider.ObserveGuideError(clock.Now)!.Value;
        Assert.Equal(clock.Now, peak.Timestamp);
        Assert.Equal(3.3, peak.ErrorPixels, 9);
        clock.Advance(SimulatedGuider.DisturbanceHalfLife);
        Assert.Equal(1.8, guider.ObserveGuideError(clock.Now)!.Value.ErrorPixels, 9);
        clock.Advance(SimulatedGuider.DisturbanceHalfLife);
        Assert.Equal(1.05, guider.ObserveGuideError(clock.Now)!.Value.ErrorPixels, 9);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0.3, guider.ObserveGuideError(clock.Now)!.Value.ErrorPixels, 6);
    }

    [Fact]
    public async Task Simulator_AfterADither_SettlesOnlyOnceTheDisturbanceHasStayedLowForTheStableDuration()
    {
        var (guider, clock, _) = await CreateGuiding();
        await guider.DitherAsync(3);

        // 0.3 + 3 · 0.5^(t / 250 ms) first drops to 0.5 px at t = 1 s; stable for 1 s from there.
        var settle = guider.SettleAsync(Options(pixels: 0.5, stableMs: 1000));
        await AssertStillSettling(settle, clock, 20);
        await settle.WaitAsync(Bound);

        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
    }

    [Fact]
    public async Task Simulator_WithoutUsableObservations_NeverSettles_AndTimesOutWithoutChangingTheGuidingState()
    {
        var (guider, clock, events) = await CreateGuiding();
        var before = events.Events.Length;
        // Always low, but stale: taken three sample intervals ago.
        guider.ObservationSource = now => new GuideErrorObservation(now - 3 * Interval, 0.1);

        var settle = guider.SettleAsync(Options(stableMs: 500, timeoutMs: 2000));
        await AssertStillSettling(settle, clock, 20);

        var error = await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => settle.WaitAsync(Bound));
        Assert.IsAssignableFrom<TimeoutException>(error);
        Assert.IsNotAssignableFrom<OperationCanceledException>(error);
        Assert.Contains("did not settle within 2 s", error.Message);
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        Assert.Equal(DeviceConnectionState.Connected, guider.ConnectionState);
        Assert.Equal(before, events.Events.Length);
    }

    [Fact]
    public async Task Simulator_AnObservationAboveTheThreshold_RestartsTheStableInterval()
    {
        var (guider, clock, _) = await CreateGuiding();
        var start = clock.Now;
        guider.ObservationSource = now =>
            new GuideErrorObservation(now, now - start == TimeSpan.FromMilliseconds(500) ? 2.0 : 0.3);

        var settle = guider.SettleAsync(Options(stableMs: 1000));
        // Without the spike at 0.5 s it would settle at 1 s; with it, at 1.6 s.
        await AssertStillSettling(settle, clock, 16);
        await settle.WaitAsync(Bound);
    }

    [Fact]
    public async Task Simulator_CancelledBeforeSettling_NeverStarts()
    {
        var (guider, _, _) = await CreateGuiding();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guider.SettleAsync(Options(), cts.Token));

        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
    }

    [Fact]
    public async Task Simulator_CancelledWhileSettling_StopsWaitingAtOnce_AndKeepsGuiding()
    {
        var (guider, clock, events) = await CreateGuiding();
        var before = events.Events.Length;
        using var cts = new CancellationTokenSource();

        var settle = guider.SettleAsync(Options(), cts.Token);
        await AssertStillSettling(settle, clock, 3);
        await clock.NextDelay();
        await cts.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settle.WaitAsync(Bound));
        Assert.IsNotType<GuidingSettleTimeoutException>(error);
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        Assert.Equal(before, events.Events.Length);

        // Guiding is still healthy: a new settle wait succeeds.
        var again = guider.SettleAsync(Options(stableMs: 300));
        await AssertStillSettling(again, clock, 3);
        await again.WaitAsync(Bound);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("disconnect")]
    public async Task Simulator_StoppingGuidingOrDisconnectingWhileSettling_EndsTheWaitWithoutSuccess(string interruption)
    {
        var (guider, clock, _) = await CreateGuiding();

        var settle = guider.SettleAsync(Options());
        await AssertStillSettling(settle, clock, 2);
        await clock.NextDelay();
        // No clock movement: the interruption alone ends the wait.
        var interrupt = interruption == "stop" ? guider.StopGuidingAsync() : guider.DisconnectAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => settle.WaitAsync(Bound));
        Assert.Contains("stopped guiding while settling", error.Message);
        await interrupt.WaitAsync(Bound);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
    }

    [Fact]
    public async Task Simulator_GuidingThatStopsAndRestarts_DoesNotRescueAnEarlierSettleWait()
    {
        var (guider, clock, _) = await CreateGuiding();

        var settle = guider.SettleAsync(Options());
        await clock.NextDelay();
        await guider.StopGuidingAsync();
        await guider.StartGuidingAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => settle.WaitAsync(Bound));
    }
}
