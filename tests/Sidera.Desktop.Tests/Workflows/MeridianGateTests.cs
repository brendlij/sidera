using Sidera.Core.Focusing;
using Sidera.Core.Guiding;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>What a setup that is about to expose is told by the flip of its mount, with a clock that the test sets: the exposure guard in its boundaries, the first look at the sky, and the sites.</summary>
public sealed class MeridianGateTests : IAsyncLifetime
{
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Start = new(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc);

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; private set; } = Start;
        public double RightAscensionHours => MeridianFlipTiming.LocalSiderealTimeHours(Start, Site.LongitudeDegrees) + 10.0 / 60;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
        public void SetHourAngle(double minutes) => Now = Start + TimeSpan.FromMinutes((minutes + 10) / 1.00273790935);
    }

    private readonly SideraRuntimeHost _host = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private MeridianFlipGroup Group(Clock clock, ObservingSite? site, List<MeridianFlipStateChanged>? events = null)
    {
        _host.AddSimulatedCamera(new("camera.a"), "Camera A", 1);
        _host.AddSimulatedMount(new("mount.1"), "Mount 1");
        var rig = new Rig(new("rig.a"), "Main", new("camera.a"), new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176), null, null, null, null, new("mount.1"));
        var plan = new MeridianFlipPlan(
            new("mount.1"), new MeridianFlipSettings { Enabled = true }, new CelestialCoordinates(clock.RightAscensionHours, 41.3), "M31", null, rig, [rig],
            new AutofocusOptions(TimeSpan.FromSeconds(1), 400, 7), new GuidingSettleOptions(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)), 1.5);
        if (events is not null)
        {
            _host.EventBus.Subscribe<MeridianFlipStateChanged>((e, _) =>
            {
                events.Add(e);
                return Task.CompletedTask;
            });
        }

        return new MeridianFlipGroup(plan, new MeridianFlipServices(_host.DeviceRegistry, Events: _host.EventBus, Time: clock, Site: () => site));
    }

    [Theory]
    [InlineData(-60.0, 300.0, MeridianGateDecision.Clear)] // far before the meridian
    [InlineData(-6.0, 300.0, MeridianGateDecision.Clear)] // just before the guard
    [InlineData(-5.0, 300.0, MeridianGateDecision.Clear)] // entering the guard: 300 s end at 0, before the flip is due
    [InlineData(-2.0, 300.0, MeridianGateDecision.Wait)] // meridian in two minutes: 300 s end after the flip is due
    [InlineData(-2.0, 240.0, MeridianGateDecision.Clear)] // exactly when the flip is due
    [InlineData(-2.0, 241.0, MeridianGateDecision.Wait)]
    [InlineData(-2.0, 60.0, MeridianGateDecision.Clear)] // a short one fits
    [InlineData(0.0, 60.0, MeridianGateDecision.Clear)] // exactly on the meridian: 60 s end at +1
    [InlineData(0.0, 130.0, MeridianGateDecision.Wait)]
    [InlineData(2.01, 1.0, MeridianGateDecision.FlipDue)]
    [InlineData(14.99, 1.0, MeridianGateDecision.FlipDue)]
    [InlineData(15.5, 1.0, MeridianGateDecision.Overdue)]
    public async Task TheGate_AsksTheSky_WithTheMeridianBoundaries(double hourAngleMinutes, double exposureSeconds, MeridianGateDecision expected)
    {
        var clock = new Clock();
        var group = Group(clock, Site);
        clock.SetHourAngle(-60);
        await group.EvaluateAsync(1, CancellationToken.None); // the first look: the crossing is still ahead

        clock.SetHourAngle(hourAngleMinutes);
        var decision = await group.EvaluateAsync(exposureSeconds, CancellationToken.None);

        Assert.Equal(expected, decision);
    }

    [Fact]
    public async Task ATargetThatIsAlreadyWestOfTheMeridianAtTheFirstLook_NeedsNoFlip_AndStaysClear()
    {
        var clock = new Clock();
        var group = Group(clock, Site);
        clock.SetHourAngle(40);

        Assert.Equal(MeridianGateDecision.Clear, await group.EvaluateAsync(300, CancellationToken.None));
        Assert.True(group.Passed);
        clock.SetHourAngle(-60); // the sky does not matter any more
        Assert.Equal(MeridianGateDecision.Clear, await group.EvaluateAsync(300, CancellationToken.None));
    }

    [Fact]
    public async Task TheHourAngle_FollowsTheClock_WithTheRightWrap()
    {
        var clock = new Clock();
        var group = Group(clock, Site);

        clock.SetHourAngle(-10);
        Assert.Equal(-10, group.HourAngleMinutes(), 1);
        clock.SetHourAngle(90);
        Assert.Equal(90, group.HourAngleMinutes(), 1);
        clock.SetHourAngle(-60 * 13 + 30); // 12.5 hours before: the same sky as 11.5 hours after, wrapped
        Assert.InRange(group.HourAngleMinutes(), -12 * 60, 12 * 60);
        await Task.CompletedTask;
    }

    [Fact]
    public void WithoutASite_AndWithAMountThatReportsNone_TheFlipSaysSo_InsteadOfIgnoringTheMeridian()
    {
        var group = Group(new Clock(), site: null); // the mount of this test is not connected: it reports no site

        var ex = Assert.Throws<MeridianFlipFailedException>(() => group.HourAngleMinutes());

        Assert.Contains("observing site", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASiteOfTheSettings_TheSiteOfAConnectedMountIsUsed()
    {
        var clock = new Clock();
        var group = Group(clock, site: null);
        foreach (var device in _host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        clock.SetHourAngle(-10);

        Assert.InRange(group.HourAngleMinutes(), -10.5, -9.5); // the simulated mount stands at 50.1 N, 8.6 E, like the site of the test
    }

    [Fact]
    public async Task TheStates_ArePublishedWhenTheyChange_NotOnEveryLook()
    {
        var clock = new Clock();
        var events = new List<MeridianFlipStateChanged>();
        var group = Group(clock, Site, events);
        clock.SetHourAngle(-60);
        await group.EvaluateAsync(60, CancellationToken.None);
        clock.SetHourAngle(-4);
        await group.EvaluateAsync(60, CancellationToken.None);
        await group.EvaluateAsync(60, CancellationToken.None);
        clock.SetHourAngle(-1);
        await group.EvaluateAsync(300, CancellationToken.None);
        await group.EvaluateAsync(300, CancellationToken.None);

        Assert.Equal([MeridianFlipState.Approaching, MeridianFlipState.HoldingForSafePoint], events.Select(e => e.State));
        Assert.All(events, e => Assert.Equal(new[] { "Main" }, e.Setups));
        Assert.InRange(events[1].HourAngleHours ?? 99, -0.03, -0.01);
    }
}
