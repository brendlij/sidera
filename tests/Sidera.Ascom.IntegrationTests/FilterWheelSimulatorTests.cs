using Sidera.Ascom.Discovery;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.FilterWheels;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Xunit.Abstractions;

namespace Sidera.Ascom.IntegrationTests;

/// <summary>The filter wheel through the real ASCOM COM layer, against the ASCOM filter wheel simulator (<c>SIDERA_ASCOM_TESTS=1</c>). The simulator is software: nothing moves.</summary>
public sealed class FilterWheelSimulatorTests(ITestOutputHelper output)
{
    private const string SimulatorProgId = "ASCOM.Simulator.FilterWheel";
    private static readonly ComAscomDriverFactory Drivers = new();

    private AscomFilterWheel NewSimulator() => new(new DeviceId("wheel.sim"), "Filter Wheel Simulator", SimulatorProgId, Drivers);

    [AscomFact]
    public async Task Discovery_FindsTheFilterWheelSimulator()
    {
        var found = await new AscomDiscovery().DiscoverAsync(AscomDeviceKind.FilterWheel);

        Assert.True(found.PlatformAvailable);
        Assert.Null(found.Problem);
        Assert.Contains(found.Drivers, d => d.ProgId == SimulatorProgId);
    }

    [AscomFact]
    public async Task TheFilterWheelSimulator_Connects_ListsItsFilters_AndTurnsToASlotAndBack()
    {
        var wheel = NewSimulator();
        await wheel.ConnectAsync();
        try
        {
            output.WriteLine($"filters {string.Join(", ", wheel.Slots.Select(s => s.Name))}, at {wheel.CurrentSlot.Name}");
            Assert.True(wheel.Slots.Count >= 2);
            var start = wheel.CurrentSlot.Index;
            var other = (start + 1) % wheel.Slots.Count;

            await wheel.MoveToSlotAsync(other);
            Assert.Equal(other, wheel.CurrentSlot.Index);
            Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);

            await wheel.MoveToSlotAsync(start);
            Assert.Equal(start, wheel.CurrentSlot.Index);
        }
        finally
        {
            await wheel.DisconnectAsync();
        }

        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
    }
}
