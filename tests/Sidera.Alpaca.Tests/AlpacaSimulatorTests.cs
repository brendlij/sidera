using Sidera.Core;
using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Xunit.Abstractions;

namespace Sidera.Alpaca.Tests;

/// <summary>
/// A test against the ASCOM Alpaca simulators (OmniSim, or SkyHub Core with a simulator). It runs when a simulator answers (and not with <c>SIDERA_ALPACA_TESTS=0</c>), and talks to the address in
/// <c>SIDERA_ALPACA_SERVER</c> (default <c>127.0.0.1:32323</c>). It refuses every host but this computer and the one address that belongs to the owner of this repository: a test never
/// touches other equipment on the network, whatever the variable says. Start the simulator bound to the loopback: <c>ascom.alpaca.simulators.exe --urls=http://127.0.0.1:32323</c>.
/// </summary>
public sealed class AlpacaSimulatorFactAttribute : FactAttribute
{
    public const string Variable = "SIDERA_ALPACA_TESTS";
    public const string ServerVariable = "SIDERA_ALPACA_SERVER";

    /// <summary>The only hosts a test may talk to.</summary>
    public static readonly string[] AllowedHosts = ["127.0.0.1", "localhost", "::1", "127.0.0.1"];

    public AlpacaSimulatorFactAttribute()
    {
        if (SideraEnvironment.Get(Variable)?.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off")
        {
            Skip = $"{Variable}=0.";
        }
        else if (!AlpacaEndpoint.TryParse(Server(), out var endpoint, out _) || !AllowedHosts.Contains(endpoint!.Host, StringComparer.OrdinalIgnoreCase))
        {
            Skip = $"{ServerVariable} must name this computer ({string.Join(", ", AllowedHosts)}); the tests do not touch other devices.";
        }
        else if (!Answers(endpoint))
        {
            Skip = "Needs an Alpaca simulator on this computer: ascom.alpaca.simulators.exe --urls=http://127.0.0.1:32323";
        }
    }

    // Only the allowed host is ever contacted, and only after it was checked above.
    private static bool Answers(AlpacaEndpoint endpoint)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static string Server() => SideraEnvironment.Get(ServerVariable) ?? "127.0.0.1:32323";

    public static AlpacaEndpoint Endpoint(int deviceNumber = 0)
    {
        Assert.True(AlpacaEndpoint.TryParse(Server(), out var endpoint, out _));
        Assert.Contains(endpoint!.Host, AllowedHosts, StringComparer.OrdinalIgnoreCase);
        return new AlpacaEndpoint(endpoint.Host, endpoint.Port, deviceNumber);
    }
}

public sealed class AlpacaSimulatorTests(ITestOutputHelper output)
{
    [AlpacaSimulatorFact]
    public async Task Probing_TheSimulator_ListsItsDome()
    {
        var endpoint = AlpacaSimulatorFactAttribute.Endpoint();

        var result = await new AlpacaDiscovery().ProbeAsync(endpoint.Host, endpoint.Port);

        Assert.Null(result.Problem);
        var server = Assert.Single(result.Servers);
        Assert.NotEmpty(server.Domes);
        output.WriteLine(string.Join("; ", server.Devices.Select(d => $"{d.DeviceType} {d.DeviceNumber}: {d.Name}")));
    }

    [AlpacaSimulatorFact]
    public async Task Discovery_AskingTheLocalHost_FindsTheSimulator()
    {
        // Unicast to this computer only; no broadcast.
        var endpoint = AlpacaSimulatorFactAttribute.Endpoint();

        var result = await new AlpacaDiscovery().DiscoverAsync(new AlpacaDiscoveryRequest { Hosts = [endpoint.Host], Timeout = TimeSpan.FromSeconds(3) });

        Assert.Null(result.Problem);
        Assert.Contains(result.Servers, s => s.Port == endpoint.Port && s.Domes.Any());
    }

    [AlpacaSimulatorFact]
    public async Task TheDomeSimulator_ConnectsReadsAndMoves()
    {
        using var dome = new AlpacaDome(new DeviceId("dome.sim"), "Dome Simulator", AlpacaSimulatorFactAttribute.Endpoint(), options: new AlpacaDomeOptions { PollInterval = TimeSpan.FromMilliseconds(500) });
        await dome.ConnectAsync();
        try
        {
            var capabilities = dome.Capabilities.Value!;
            output.WriteLine($"{capabilities.Driver.Name} / {capabilities.Driver.DriverInfo}: shutter {capabilities.CanSetShutter}, azimuth {capabilities.CanSetAzimuth}, park {capabilities.CanPark}, home {capabilities.CanFindHome}");
            Assert.Equal(DeviceConnectionState.Connected, dome.ConnectionState);
            Assert.True(capabilities.CanSetShutter);

            // A simulator that was stopped in the middle of a move before reports an error state; closing is allowed from it.
            if (dome.ShutterState != DomeShutterState.Closed)
            {
                await dome.CloseShutterAsync();
            }

            await dome.OpenShutterAsync();
            Assert.Equal(DomeShutterState.Open, dome.ShutterState);
            await dome.SlewToAzimuthAsync(90);
            Assert.True(Math.Abs(dome.Azimuth!.Value - 90) <= 1.0);
            await dome.CloseShutterAsync();
            Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
            if (capabilities.CanPark)
            {
                await dome.ParkAsync();
                Assert.True(dome.Telemetry!.AtPark);
            }

            if (capabilities.CanFindHome)
            {
                await dome.FindHomeAsync();
                Assert.True(dome.Telemetry!.AtHome);
            }
        }
        finally
        {
            await dome.DisconnectAsync();
        }
    }

    [AlpacaSimulatorFact]
    public async Task TheDomeSimulator_CanBeStoppedWhileTheShutterMoves()
    {
        using var dome = new AlpacaDome(new DeviceId("dome.sim"), "Dome Simulator", AlpacaSimulatorFactAttribute.Endpoint(), options: new AlpacaDomeOptions { OperationPollInterval = TimeSpan.FromMilliseconds(100) });
        await dome.ConnectAsync();
        try
        {
            if (dome.ShutterState != DomeShutterState.Closed)
            {
                await dome.CloseShutterAsync();
            }

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dome.OpenShutterAsync(cancel.Token));
            await dome.RefreshAsync();
            output.WriteLine($"After the stop the shutter reports {dome.ShutterState}, moving: {dome.IsMoving}");
            Assert.False(dome.IsMoving);
            if (dome.ShutterState != DomeShutterState.Closed)
            {
                await dome.CloseShutterAsync(); // leave the simulator closed
            }
        }
        finally
        {
            await dome.DisconnectAsync();
        }
    }
}
