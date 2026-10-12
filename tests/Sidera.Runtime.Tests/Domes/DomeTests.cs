using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Domes;

public sealed class SimulatedDomeTests
{
    private static async Task<SimulatedDome> ConnectedAsync(bool roof = false, double speed = 3600, double shutterMilliseconds = 40)
    {
        var dome = new SimulatedDome(new DeviceId("dome.sim"), shutterOnly: roof, shutterTime: TimeSpan.FromMilliseconds(shutterMilliseconds), degreesPerSecond: speed);
        await dome.ConnectAsync();
        return dome;
    }

    [Fact]
    public async Task ItSaysWhatItCanDo_OnlyWhileConnected()
    {
        var dome = new SimulatedDome(new DeviceId("dome.sim"));
        Assert.False(dome.Capabilities.IsAvailable);
        Assert.Null(dome.Telemetry);
        Assert.Equal(DomeShutterState.Unknown, dome.ShutterState);

        await dome.ConnectAsync();

        var c = dome.Capabilities.Value!;
        Assert.True(c.CanSetShutter && c.CanSetAzimuth && c.CanPark && c.CanFindHome);
        Assert.False(c.IsShutterOnly);
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        await dome.DisconnectAsync();
        Assert.False(dome.Capabilities.IsAvailable);
    }

    [Fact]
    public async Task TheShutter_OpensAndCloses_AndSaysItWhileItMoves()
    {
        var dome = await ConnectedAsync(shutterMilliseconds: 150);

        var opening = dome.OpenShutterAsync();
        await Task.Delay(40);
        Assert.Equal(DomeShutterState.Opening, dome.ShutterState);
        Assert.True(dome.IsMoving);
        await opening;
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);

        await dome.CloseShutterAsync();
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        Assert.False(dome.IsMoving);
    }

    [Fact]
    public async Task ARoof_HasNoAzimuth_AndCannotBeTurned()
    {
        var roof = await ConnectedAsync(roof: true);

        Assert.True(roof.Capabilities.Value!.IsShutterOnly);
        Assert.Null(roof.Azimuth);
        Assert.Null(roof.Telemetry!.AtPark);
        await Assert.ThrowsAsync<InvalidOperationException>(() => roof.SlewToAzimuthAsync(90));
        await roof.OpenShutterAsync();
        Assert.Equal(DomeShutterState.Open, roof.ShutterState);
    }

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(350, 10, 10)]
    [InlineData(10, 370, 10)]
    [InlineData(0, -90, 270)]
    public async Task TurningTheDome_ArrivesAtTheAzimuth(double start, double target, double expected)
    {
        var dome = new SimulatedDome(new DeviceId("dome.sim"), degreesPerSecond: 7200, startAzimuth: start);
        await dome.ConnectAsync();

        await dome.SlewToAzimuthAsync(target);

        Assert.Equal(expected, dome.Azimuth!.Value, 6);
        Assert.False(dome.IsMoving);
        Assert.False(dome.Telemetry!.AtPark);
    }

    [Fact]
    public async Task ParkAndHome_ArriveAndSayIt()
    {
        var dome = new SimulatedDome(new DeviceId("dome.sim"), degreesPerSecond: 7200, startAzimuth: 120);
        await dome.ConnectAsync();

        await dome.ParkAsync();
        Assert.True(dome.Telemetry!.AtPark);
        Assert.Equal(0, dome.Azimuth);

        await dome.SlewToAzimuthAsync(45);
        Assert.False(dome.Telemetry!.AtPark);
        await dome.FindHomeAsync();
        Assert.True(dome.Telemetry!.AtHome);
    }

    [Fact]
    public async Task AHaltInTheMiddleOfAShutterMove_EndsItAsCancelled_AndTheShutterReportsAnError_ButCanBeClosed()
    {
        var dome = await ConnectedAsync(shutterMilliseconds: 2000);
        var opening = dome.OpenShutterAsync();
        await Task.Delay(40);

        await dome.HaltAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(DomeShutterState.Error, dome.ShutterState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.OpenShutterAsync()); // not opened from an error
        var closing = dome.CloseShutterAsync(); // but closed: the safe direction
        await closing;
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
    }

    [Fact]
    public async Task CancellingAMove_StopsItWhereItStands()
    {
        var dome = new SimulatedDome(new DeviceId("dome.sim"), degreesPerSecond: 90);
        await dome.ConnectAsync();
        using var cancel = new CancellationTokenSource(80);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dome.SlewToAzimuthAsync(180, cancel.Token));

        Assert.False(dome.IsMoving);
        Assert.InRange(dome.Azimuth!.Value, 0.5, 179);
    }

    [Fact]
    public async Task OneThingAtATime_AndNothingWithoutAConnection()
    {
        var dome = new SimulatedDome(new DeviceId("dome.sim"), shutterTime: TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.OpenShutterAsync());
        await dome.ConnectAsync();
        var opening = dome.OpenShutterAsync();
        await Task.Delay(30);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.CloseShutterAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.DisconnectAsync());
        await opening;
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task TheHost_RegistersADome_AndTheOperationServiceMovesIt()
    {
        await using var host = new SideraRuntimeHost();
        var dome = host.AddSimulatedDome(new DeviceId("dome.sim"), "Dome", shutterTime: TimeSpan.FromMilliseconds(30));
        await host.DeviceOperations.ConnectAsync(dome.Id);

        await host.DeviceOperations.OpenDomeShutterAsync(dome.Id);
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);
        await host.DeviceOperations.SlewDomeToAsync(dome.Id, 100);
        await host.DeviceOperations.CloseDomeShutterAsync(dome.Id);
        await host.DeviceOperations.ParkDomeAsync(dome.Id);

        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        Assert.True(dome.Telemetry!.AtPark);
        Assert.Contains(host.DeviceRegistry.GetAll(), d => d is IDomeControl);
    }

    [Fact]
    public async Task AHalt_ReachesTheDome_WhileAnOperationHoldsIt()
    {
        await using var host = new SideraRuntimeHost();
        var dome = host.AddSimulatedDome(new DeviceId("dome.sim"), "Dome", shutterTime: TimeSpan.FromSeconds(5));
        await host.DeviceOperations.ConnectAsync(dome.Id);
        var opening = host.DeviceOperations.OpenDomeShutterAsync(dome.Id);
        await Task.Delay(50);

        await host.DeviceOperations.HaltDomeAsync(dome.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(DomeShutterState.Error, dome.ShutterState);
    }
}
