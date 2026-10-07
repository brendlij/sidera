using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

/// <summary>Cooling, warming, parking and tracking as steps: gentle, capability-checked, and holding what they move.</summary>
public sealed class DeviceOperationActionTests : IAsyncLifetime
{
    private readonly SideraRuntimeHost _host = new();
    private static readonly DeviceId Camera = new("camera.main");
    private static readonly DeviceId Mount = new("mount.1");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(SimulatedCamera Camera, SimulatedMount Mount)> ConnectedAsync()
    {
        var camera = _host.AddSimulatedCamera(Camera, "Camera", 1);
        var mount = _host.AddSimulatedMount(Mount, "Mount", TimeSpan.FromMilliseconds(10));
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        return (camera, mount);
    }

    private DeviceOperationAction Action(DeviceOperation operation, DeviceId device, double celsius = 0, double minutes = 0, TimeSpan? step = null) =>
        new(_host.DeviceRegistry, operation, device, celsius, minutes, step ?? TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task CoolingMovesTheSetPointInSteps_NotInOneJump_AndLeavesTheCoolerOn()
    {
        var (camera, _) = await ConnectedAsync();
        var seen = new SortedSet<double>();
        using var cts = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (camera.Settings?.TargetTemperature is { } target)
                {
                    seen.Add(Math.Round(target, 1));
                }

                await Task.Delay(5);
            }
        });

        // From 20 °C to 19.5 °C over about half a second, a step every 100 ms: the sensor is within the tolerance throughout, so the action does not wait for it.
        await Action(DeviceOperation.CoolCamera, Camera, 19.5, 0.01).ExecuteAsync(NoContext.Instance, CancellationToken.None);
        await cts.CancelAsync();
        await watcher;

        Assert.True(seen.Count >= 3, "the set point passes through steps: " + string.Join(", ", seen));
        Assert.Equal(19.5, camera.Settings!.TargetTemperature!.Value, 1);
        Assert.True(camera.Settings.CoolerOn);
    }

    [Fact]
    public async Task Warming_SwitchesTheCoolerOff()
    {
        var (camera, _) = await ConnectedAsync();
        await camera.ApplyAsync(new CameraSettings { TargetTemperature = 5, CoolerOn = true });

        // The simulated sensor is still at ambient (it has not had time to cool), which is above the temperature a sensor is warmed to: the cooler is switched off at once.
        await Action(DeviceOperation.WarmCamera, Camera, 0, 0.01).ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.False(camera.Settings!.CoolerOn);
    }

    [Fact]
    public async Task ParkingUnparkingAndTracking_DoWhatTheyAreCalled()
    {
        var (_, mount) = await ConnectedAsync();

        await Action(DeviceOperation.Unpark, Mount).ExecuteAsync(NoContext.Instance, CancellationToken.None);
        Assert.False(mount.Telemetry!.AtPark);

        await Action(DeviceOperation.TrackingOn, Mount).ExecuteAsync(NoContext.Instance, CancellationToken.None);
        Assert.True(mount.Telemetry!.Tracking);
        await Action(DeviceOperation.TrackingOff, Mount).ExecuteAsync(NoContext.Instance, CancellationToken.None);
        Assert.False(mount.Telemetry!.Tracking);

        await Action(DeviceOperation.Park, Mount).ExecuteAsync(NoContext.Instance, CancellationToken.None);
        Assert.True(mount.Telemetry!.AtPark);
    }

    [Fact]
    public async Task ADeviceThatIsNotConnected_OrIsNotTheRightKind_IsRefusedClearly()
    {
        var camera = _host.AddSimulatedCamera(Camera, "Camera", 1);
        _host.AddSimulatedMount(Mount, "Mount", TimeSpan.FromMilliseconds(10));

        var notConnected = await Assert.ThrowsAsync<InvalidOperationException>(() => Action(DeviceOperation.CoolCamera, Camera, -10).ExecuteAsync(NoContext.Instance, CancellationToken.None));
        Assert.Contains("is not connected", notConnected.Message, StringComparison.Ordinal);

        await camera.ConnectAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => Action(DeviceOperation.Park, Camera).ExecuteAsync(NoContext.Instance, CancellationToken.None)); // a camera is not a mount
    }

    [Fact]
    public void TheClaims_HoldTheCameraForCooling_AndTheMountAndItsStabilityForTheMountOperations()
    {
        var cool = Action(DeviceOperation.CoolCamera, Camera, -10);
        var park = Action(DeviceOperation.Park, Mount);

        Assert.Equal([ResourceClaim.Exclusive(ResourceId.ForDevice(Camera))], cool.Claims);
        Assert.Equal([ResourceClaim.Exclusive(ResourceId.ForDevice(Mount)), ResourceClaim.Exclusive(ResourceId.ForMountStability(Mount))], park.Claims);
        Assert.Equal("Cool camera to -10 °C", cool.Name);
        Assert.Equal("Park mount", park.Name);
    }
}
