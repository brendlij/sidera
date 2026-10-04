using Sidera.Core.Devices;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Devices;

public class DeviceRegistryTests
{
    [Fact]
    public void Register_AddsDevice()
    {
        var registry = new DeviceRegistry();
        var camera = new SimulatedCamera(new DeviceId("cam-1"));

        registry.Register(camera);

        Assert.Same(camera, Assert.Single(registry.GetAll()));
    }

    [Fact]
    public void TryGet_ReturnsRegisteredDevice()
    {
        var registry = new DeviceRegistry();
        var camera = new SimulatedCamera(new DeviceId("cam-1"));
        registry.Register(camera);

        var found = registry.TryGet(new DeviceId("cam-1"), out var device);

        Assert.True(found);
        Assert.Same(camera, device);
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnknownId()
    {
        var registry = new DeviceRegistry();

        Assert.False(registry.TryGet(new DeviceId("missing"), out var device));
        Assert.Null(device);
    }

    [Fact]
    public void Register_RejectsDuplicateId()
    {
        var registry = new DeviceRegistry();
        registry.Register(new SimulatedCamera(new DeviceId("cam-1")));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new SimulatedCamera(new DeviceId("cam-1"), "Other")));
        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void Unregister_RemovesDevice()
    {
        var registry = new DeviceRegistry();
        var id = new DeviceId("cam-1");
        registry.Register(new SimulatedCamera(id));

        Assert.True(registry.Unregister(id));
        Assert.False(registry.TryGet(id, out _));
        Assert.Empty(registry.GetAll());
        Assert.False(registry.Unregister(id));
    }
}
