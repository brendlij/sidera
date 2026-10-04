using Sidera.Core.Devices;
using Sidera.Core.Guiding;

namespace Sidera.Runtime.Tests.Guiding;

public class GuiderHostTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);

    private static readonly DeviceId GuiderId = new("guider.main");

    [Fact]
    public async Task AddSimulatedGuider_RegistersItUnderItsId_WithTheGivenOptions()
    {
        await using var host = new SideraRuntimeHost();

        var guider = host.AddSimulatedGuider(GuiderId, "Main guider", Quick, Quick);

        Assert.True(host.DeviceRegistry.TryGet(GuiderId, out var device));
        Assert.Same(guider, device);
        Assert.IsAssignableFrom<IGuider>(device);
        Assert.Equal("Main guider", guider.Name);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            host.AddSimulatedGuider(new DeviceId("guider.bad"), "Bad", startDuration: TimeSpan.Zero));
        Assert.False(host.DeviceRegistry.TryGet(new DeviceId("guider.bad"), out _));
    }

    [Fact]
    public async Task AddSimulatedGuider_RejectsADuplicateId()
    {
        await using var host = new SideraRuntimeHost();
        host.AddSimulatedGuider(GuiderId, "Main guider");

        var error = Assert.Throws<InvalidOperationException>(() => host.AddSimulatedGuider(GuiderId, "Again"));

        Assert.Contains("'guider.main' is already registered", error.Message);
    }

    [Fact]
    public async Task SeveralGuiders_Coexist_AndAreSelectedById()
    {
        await using var host = new SideraRuntimeHost();
        var main = host.AddSimulatedGuider(GuiderId, "Main", Quick, Quick);
        var oag = host.AddSimulatedGuider(new DeviceId("guider.oag"), "OAG", Quick, Quick);
        await main.ConnectAsync();
        await oag.ConnectAsync();

        await host.DeviceOperations.StartGuidingAsync(oag.Id);

        Assert.Equal(GuidingState.Idle, main.GuidingState);
        Assert.Equal(GuidingState.Guiding, oag.GuidingState);
        Assert.Equal(2, host.DeviceRegistry.GetAll().OfType<IGuider>().Count());
    }

    [Fact]
    public async Task SimulatedGuider_PublishesIntoTheHostStateStore()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Main guider", Quick, Quick);

        await guider.ConnectAsync();
        Assert.True(host.StateStore.TryGet(GuiderId, out var connected));
        Assert.Equal(new DeviceState(GuiderId, DeviceConnectionState.Connected), connected);

        await guider.StartGuidingAsync();
        Assert.True(host.StateStore.TryGet(GuiderId, out var guiding));
        Assert.Equal(
            new DeviceState(GuiderId, DeviceConnectionState.Connected, GuidingState: GuidingState.Guiding),
            guiding);
    }

    [Fact]
    public async Task AddSimulatedGuider_ForwardsTheDitherDuration()
    {
        await using var host = new SideraRuntimeHost();

        var guider = host.AddSimulatedGuider(GuiderId, "Main guider", ditherDuration: Quick);

        Assert.IsAssignableFrom<IDitherGuider>(guider);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            host.AddSimulatedGuider(new DeviceId("guider.bad"), "Bad", ditherDuration: TimeSpan.FromTicks(-1)));
        Assert.False(host.DeviceRegistry.TryGet(new DeviceId("guider.bad"), out _));
    }

    [Fact]
    public async Task SimulatedDither_IsProjectedIntoTheHostStateStore()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Main guider", Quick, Quick, Quick);
        var projected = new List<GuidingState?>();
        // The store subscribes first, so by the time this subscriber runs it has already applied the event.
        host.EventBus.Subscribe<GuidingStateChanged>((_, _) =>
        {
            host.StateStore.TryGet(GuiderId, out var state);
            projected.Add(state?.GuidingState);
            return Task.CompletedTask;
        });
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();

        await guider.DitherAsync(1.5);

        Assert.Equal(
            new GuidingState?[] { GuidingState.Starting, GuidingState.Guiding, GuidingState.Dithering, GuidingState.Guiding },
            projected);
        Assert.True(host.StateStore.TryGet(GuiderId, out var final));
        Assert.Equal(
            new DeviceState(GuiderId, DeviceConnectionState.Connected, GuidingState: GuidingState.Guiding),
            final);
    }

    [Fact]
    public async Task Stop_DisconnectsAnActivelyGuidingSimulator_AndProjectsDisconnectedIdle()
    {
        await using var host = new SideraRuntimeHost();
        var guider = host.AddSimulatedGuider(GuiderId, "Main guider", Quick, Quick);
        host.Start();
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();

        await host.StopAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, guider.ConnectionState);
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
        Assert.True(host.StateStore.TryGet(GuiderId, out var state));
        Assert.Equal(
            new DeviceState(GuiderId, DeviceConnectionState.Disconnected, GuidingState: GuidingState.Idle),
            state);
    }
}
