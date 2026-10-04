using Sidera.Core;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Xunit.Abstractions;

namespace Sidera.Phd2.Tests;

/// <summary>A test against a real PHD2; it runs only when its gate is set (<c>SIDERA_PHD2_TESTS=1</c> and so on).</summary>
public sealed class Phd2FactAttribute : FactAttribute
{
    public Phd2FactAttribute(string gate)
    {
        if (SideraEnvironment.Get(gate) != "1")
        {
            Skip = $"Needs a running PHD2 with its server enabled. Set {gate}=1 to run it.";
        }
    }
}

/// <summary>
/// The PHD2 guider against a real PHD2 (host and port from <c>SIDERA_PHD2_HOST</c> and <c>SIDERA_PHD2_PORT</c>, 127.0.0.1:4400 by default).
/// <c>SIDERA_PHD2_TESTS=1</c> only connects and reads; it moves nothing. Guiding needs <c>SIDERA_PHD2_GUIDING_OK=1</c> (the equipment of PHD2
/// connected, a star field to guide on) and a dither needs <c>SIDERA_PHD2_DITHER_OK=1</c>. A sky without a guide star is no failure of Sidera:
/// the test says that it could not run and why.
/// </summary>
public sealed class RealPhd2Tests(ITestOutputHelper output)
{
    private static Phd2Endpoint Endpoint =>
        new(SideraEnvironment.Get("SIDERA_PHD2_HOST") ?? Phd2Endpoint.DefaultHost,
            int.TryParse(SideraEnvironment.Get("SIDERA_PHD2_PORT"), out var port) ? port : Phd2Endpoint.DefaultPort);

    private Phd2Guider NewGuider() => new(new DeviceId("guider.real"), "Real PHD2", Endpoint, options: new Phd2GuiderOptions
    {
        DefaultSettle = new GuidingSettleOptions(1.5, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60)),
        StartTimeout = TimeSpan.FromMinutes(10),
    });

    [Phd2Fact("SIDERA_PHD2_TESTS")]
    public async Task ThePhd2Guider_ConnectsToARealPhd2_ReadsItsStateAndSetup_AndDisconnectsLeavingPhd2AsItIs()
    {
        await using var guider = NewGuider();
        var before = Environment.TickCount64;

        await guider.ConnectAsync();

        var info = guider.Info!;
        var telemetry = guider.Telemetry!;
        output.WriteLine($"connected to {Endpoint} in {Environment.TickCount64 - before} ms; PHD2 {guider.Capabilities.Value!.Driver.DriverVersion}");
        output.WriteLine($"state {guider.GuidingState}; profile {info.Profile}; guide camera {info.GuideCamera}; mount {info.Mount}; calibrated {info.IsCalibrated}; equipment connected {info.EquipmentConnected}");
        output.WriteLine($"exposure {telemetry.ExposureSeconds} s; pixel scale {telemetry.PixelScaleArcsecPerPixel}");
        Assert.Equal(DeviceConnectionState.Connected, guider.ConnectionState);
        Assert.True(guider.Capabilities.IsAvailable);
        Assert.NotNull(info.Profile);
        Assert.Equal(0, guider.History.Count);

        await guider.RefreshAsync();
        await guider.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, guider.ConnectionState);

        await guider.ConnectAsync(); // a manual reconnect works
        Assert.Equal(DeviceConnectionState.Connected, guider.ConnectionState);
        Assert.Equal(0, guider.History.Count);
    }

    // Starts guiding; false (and the reason in the output) when PHD2 cannot, for lack of equipment or of a star.
    private async Task<bool> TryStartAsync(Phd2Guider guider)
    {
        try
        {
            await guider.StartGuidingAsync();
            return true;
        }
        catch (Phd2Exception ex)
        {
            output.WriteLine("NOT RUN (environment): PHD2 could not start guiding: " + ex.Message);
            return false;
        }
    }

    [Phd2Fact("SIDERA_PHD2_GUIDING_OK")]
    public async Task ThePhd2Guider_Guides_ShowsSamplesAndRms_Stops_AndStartsAgain()
    {
        await using var guider = NewGuider();
        await guider.ConnectAsync();
        if (!await TryStartAsync(guider))
        {
            return;
        }

        output.WriteLine($"guiding; calibrated {guider.Info?.IsCalibrated}");
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        await Task.Delay(TimeSpan.FromSeconds(20));
        var samples = guider.History.Snapshot();
        var rms = guider.Telemetry!.Rms;
        output.WriteLine($"{samples.Length} samples in 20 s; RMS RA {rms.RaArcsec:0.00}\" Dec {rms.DecArcsec:0.00}\" total {rms.TotalArcsec:0.00}\"; SNR {guider.Telemetry.StarSnr}");
        Assert.NotEmpty(samples);
        Assert.NotNull(rms.TotalArcsec);

        await guider.StopGuidingAsync();
        Assert.Equal(GuidingState.Idle, guider.GuidingState);

        Assert.True(await TryStartAsync(guider));
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        await guider.StopGuidingAsync();
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
    }

    [Phd2Fact("SIDERA_PHD2_DITHER_OK")]
    public async Task ThePhd2Guider_DithersOnce_AndTheDitherIsOverWhenPhd2HasSettled()
    {
        await using var guider = NewGuider();
        await guider.ConnectAsync();
        if (guider.GuidingState != GuidingState.Guiding && !await TryStartAsync(guider))
        {
            return;
        }

        var settle = new GuidingSettleOptions(1.5, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60));
        output.WriteLine("dither requested: amount 1.0 px, tolerance 1.5 px, stable 8 s, timeout 60 s");
        var started = System.Diagnostics.Stopwatch.StartNew();

        await guider.DitherAsync(new DitherRequest(1.0, RaOnly: false, settle));
        output.WriteLine($"PHD2 moved the lock position after {started.Elapsed.TotalSeconds:0.0} s");
        await guider.SettleAsync(settle);

        output.WriteLine($"settled after {started.Elapsed.TotalSeconds:0.0} s; outcome {guider.Telemetry!.Settle.LastOutcome}");
        Assert.Equal(GuidingSettleOutcome.Settled, guider.Telemetry.Settle.LastOutcome);
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(8)); // not before the stable time that was asked for
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
    }
}
