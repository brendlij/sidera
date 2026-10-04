using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Guiding;

public sealed class SimulatedGuiderMeasurementsTests
{
    [Fact]
    public async Task ASimulatedGuider_ProducesSamplesAndTelemetryWhileGuiding_AndNoneBefore_OrAfter()
    {
        var guider = new SimulatedGuider(new DeviceId("guider.sim"));
        Assert.Null(guider.Telemetry);

        await guider.ConnectAsync();
        Assert.True(guider.Capabilities.Value!.ProvidesGuideTelemetry);
        await Task.Delay(700);
        Assert.Equal(0, guider.History.Count);

        await guider.StartGuidingAsync();
        await Task.Delay(1800);
        Assert.True(guider.History.Count >= 2);
        Assert.NotNull(guider.Telemetry?.Rms.TotalArcsec);
        Assert.NotNull(guider.Telemetry!.StarSnr);

        await guider.DisconnectAsync();
        Assert.Null(guider.Telemetry);
    }
}
