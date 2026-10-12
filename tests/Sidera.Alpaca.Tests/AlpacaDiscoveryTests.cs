using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sidera.Alpaca.Tests;

/// <summary>
/// Discovery against a responder on this computer's loopback (UDP) and a fake of the management API (HTTP). The discovery port is a free local port, so nothing here is sent anywhere but to
/// 127.0.0.1, and no test sends a broadcast.
/// </summary>
public sealed class AlpacaDiscoveryTests
{
    private const string Management = """
        {"Value":[
          {"DeviceName":"Alpaca Camera Sim","DeviceType":"Camera","DeviceNumber":0,"UniqueID":"a"},
          {"DeviceName":"Roof","DeviceType":"Dome","DeviceNumber":1,"UniqueID":"b"}],
         "ErrorNumber":0,"ErrorMessage":""}
        """;

    private sealed class ManagementHandler : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            var body = request.RequestUri!.AbsolutePath.EndsWith("configureddevices", StringComparison.Ordinal)
                ? Management
                : """{"Value":{"ServerName":"Observatory server","Manufacturer":"Test"},"ErrorNumber":0,"ErrorMessage":""}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static UdpClient Responder(string reply, out int port)
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var received = await udp.ReceiveAsync();
                    if (Encoding.ASCII.GetString(received.Buffer) == "alpacadiscovery1")
                    {
                        await udp.SendAsync(Encoding.ASCII.GetBytes(reply), received.RemoteEndPoint);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // The test is over.
            }
            catch (SocketException)
            {
            }
        });
        return udp;
    }

    [Fact]
    public async Task AskingAHost_FindsItsServer_AndItsDevices()
    {
        using var responder = Responder("""{"AlpacaPort":32323}""", out var discoveryPort);
        var handler = new ManagementHandler();
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(handler));

        var result = await discovery.DiscoverAsync(new AlpacaDiscoveryRequest { Hosts = ["127.0.0.1"], DiscoveryPort = discoveryPort, Timeout = TimeSpan.FromMilliseconds(600) });

        Assert.Null(result.Problem);
        var server = Assert.Single(result.Servers);
        Assert.Equal("127.0.0.1", server.Host);
        Assert.Equal(32323, server.Port);
        Assert.Equal("Observatory server", server.ServerName);
        Assert.Equal(2, server.Devices.Count);
        var dome = Assert.Single(server.Domes);
        Assert.Equal("Roof", dome.Name);
        Assert.Equal("127.0.0.1:32323/1", server.EndpointOf(dome).ToString());
        Assert.All(handler.Seen, uri => Assert.Equal("127.0.0.1", uri.Host)); // the management API of the server that answered, nobody else
        Assert.All(handler.Seen, uri => Assert.StartsWith("/management/", uri.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASilentHost_FindsNothing_AndIsNotAnError()
    {
        // A port on which nothing listens: the datagrams are lost, or refused.
        var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;
        free.Dispose();
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(new ManagementHandler()));

        var result = await discovery.DiscoverAsync(new AlpacaDiscoveryRequest { Hosts = ["127.0.0.1"], DiscoveryPort = port, Timeout = TimeSpan.FromMilliseconds(300) });

        Assert.Empty(result.Servers);
        Assert.Null(result.Problem);
    }

    [Fact]
    public async Task SomethingThatAnswersButIsNotAlpaca_IsNotAServer()
    {
        using var responder = Responder("hello, I am a printer", out var discoveryPort);
        var handler = new ManagementHandler();
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(handler));

        var result = await discovery.DiscoverAsync(new AlpacaDiscoveryRequest { Hosts = ["127.0.0.1"], DiscoveryPort = discoveryPort, Timeout = TimeSpan.FromMilliseconds(400) });

        Assert.Empty(result.Servers);
        Assert.Empty(handler.Seen); // nothing was asked of it over HTTP
    }

    [Fact]
    public async Task NothingToSearch_IsSaid_AndNothingIsSent()
    {
        var handler = new ManagementHandler();
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(handler));

        var result = await discovery.DiscoverAsync(new AlpacaDiscoveryRequest());

        Assert.Empty(result.Servers);
        Assert.Contains("Enter an address", result.Problem, StringComparison.Ordinal);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task AHostThatCannotBeFound_IsSaid()
    {
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(new ManagementHandler()));

        var result = await discovery.DiscoverAsync(new AlpacaDiscoveryRequest { Hosts = ["no such host"], Timeout = TimeSpan.FromMilliseconds(200) });

        Assert.Empty(result.Servers);
        Assert.Contains("could not be found", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbingAnAddress_ListsItsDevices_WithoutDiscovery()
    {
        var handler = new ManagementHandler();
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(handler));

        var result = await discovery.ProbeAsync("192.0.2.7", 11111);

        Assert.Null(result.Problem);
        var server = Assert.Single(result.Servers);
        Assert.Equal("192.0.2.7", server.Host);
        Assert.Single(server.Domes);
        Assert.All(handler.Seen, uri => Assert.Equal("192.0.2.7", uri.Host));
    }

    [Fact]
    public async Task ProbingAnAddressThatIsNotAnAlpacaServer_SaysSo()
    {
        var discovery = new AlpacaDiscovery(null, () => new HttpClient(new NotAlpaca()));

        var result = await discovery.ProbeAsync("192.0.2.7", 80);

        Assert.Empty(result.Servers);
        Assert.Contains("did not answer like an Alpaca server", result.Problem, StringComparison.Ordinal);
    }

    private sealed class NotAlpaca : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });
    }
}
