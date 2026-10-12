using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Alpaca;

/// <summary>A device that an Alpaca server says it has (<c>/management/v1/configureddevices</c>).</summary>
public sealed record AlpacaDeviceInfo(string Name, string DeviceType, int DeviceNumber, string? UniqueId)
{
    public bool IsDome => string.Equals(DeviceType, "Dome", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An Alpaca server that answered, and the devices it has.</summary>
public sealed record AlpacaServer(string Host, int Port, string? ServerName, IReadOnlyList<AlpacaDeviceInfo> Devices)
{
    public IEnumerable<AlpacaDeviceInfo> Domes => Devices.Where(d => d.IsDome);

    public AlpacaEndpoint EndpointOf(AlpacaDeviceInfo device) => new(Host, Port, device.DeviceNumber);
}

/// <summary>
/// What to search. Nothing on the network is asked that is not named here: <see cref="Hosts"/> are asked one by one (each by name or address), and the whole local network only when
/// <see cref="Broadcast"/> is set, which is the one thing that reaches devices nobody named.
/// </summary>
public sealed record AlpacaDiscoveryRequest
{
    /// <summary>Hosts to ask directly (unicast); only these answer.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>Also ask every device of the local network (a broadcast on every network interface).</summary>
    public bool Broadcast { get; init; }

    /// <summary>How long to wait for answers.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The Alpaca discovery port; always 32227 outside tests.</summary>
    public int DiscoveryPort { get; init; } = AlpacaDiscovery.DefaultDiscoveryPort;
}

/// <param name="Servers">The servers that answered, with their devices.</param>
/// <param name="Problem">What went wrong for the whole search or for part of it, in a sentence; <c>null</c> when it worked (even if nothing answered).</param>
public sealed record AlpacaDiscoveryResult(IReadOnlyList<AlpacaServer> Servers, string? Problem = null);

/// <summary>Finds Alpaca servers and the devices they have.</summary>
public interface IAlpacaDiscovery
{
    Task<AlpacaDiscoveryResult> DiscoverAsync(AlpacaDiscoveryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Asks one server, whose address is known, what devices it has (no discovery involved: for a server that does not answer discovery, or that is on another network).</summary>
    Task<AlpacaDiscoveryResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default);
}

/// <summary>
/// Alpaca discovery: a UDP datagram <c>alpacadiscovery1</c> to port 32227, answered with <c>{"AlpacaPort": n}</c> by every server that is there; then the management API of each server
/// (<c>/management/v1/configureddevices</c>) says what it has. A host that is named is asked by itself and only its own answer counts; a broadcast is sent only when the request says so.
/// Nothing is ever connected, switched or moved by it: it reads.
/// </summary>
public sealed class AlpacaDiscovery(ILogger? logger = null, Func<HttpClient?>? httpFactory = null) : IAlpacaDiscovery
{
    public const int DefaultDiscoveryPort = 32227;

    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("alpacadiscovery1");
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<AlpacaDiscoveryResult> DiscoverAsync(AlpacaDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hosts = request.Hosts.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hosts.Count == 0 && !request.Broadcast)
        {
            return new AlpacaDiscoveryResult([], "Enter an address to search, or search the whole network.");
        }

        var problems = new List<string>();
        var targets = new List<IPAddress>();
        foreach (var host in hosts)
        {
            try
            {
                targets.AddRange(await ResolveAsync(host, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                problems.Add($"The address '{host}' could not be found.");
            }
        }

        var answers = new Dictionary<(IPAddress Address, int Port), bool>();
        try
        {
            foreach (var answer in await AskAsync(request, targets, cancellationToken).ConfigureAwait(false))
            {
                answers.TryAdd(answer, true);
            }
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Alpaca discovery failed");
            problems.Add("The search could not be sent: " + ex.Message);
        }

        var servers = new List<AlpacaServer>();
        foreach (var (address, port) in answers.Keys)
        {
            var host = address.ToString();
            try
            {
                servers.Add(await DescribeAsync(host, port, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is AlpacaCommunicationException or AlpacaException)
            {
                _logger.LogWarning(ex, "The Alpaca server at {Host}:{Port} answered discovery but not its device list", host, port);
                problems.Add($"{host}:{port} answered the search but did not list its devices.");
            }
        }

        return new AlpacaDiscoveryResult(servers, problems.Count == 0 ? null : string.Join(' ', problems));
    }

    public async Task<AlpacaDiscoveryResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        try
        {
            return new AlpacaDiscoveryResult([await DescribeAsync(host.Trim().Trim('[', ']'), port, cancellationToken).ConfigureAwait(false)]);
        }
        catch (Exception ex) when (ex is AlpacaCommunicationException or AlpacaException)
        {
            return new AlpacaDiscoveryResult([], ex.Message);
        }
    }

    private async Task<IReadOnlyList<(IPAddress, int)>> AskAsync(AlpacaDiscoveryRequest request, List<IPAddress> targets, CancellationToken cancellationToken)
    {
        var found = new List<(IPAddress, int)>();
        var sockets = new List<(UdpClient Client, IPEndPoint Destination, bool Broadcast)>();
        try
        {
            // The named hosts: one datagram to each, from a socket of the same family. Only an answer from one of them counts.
            foreach (var address in targets)
            {
                var client = new UdpClient(new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                sockets.Add((client, new IPEndPoint(address, request.DiscoveryPort), false));
            }

            if (request.Broadcast)
            {
                foreach (var (local, broadcast) in BroadcastAddresses())
                {
                    var client = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                    sockets.Add((client, new IPEndPoint(broadcast, request.DiscoveryPort), true));
                }
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(request.Timeout);
            var receivers = sockets.Select(s => ReceiveAsync(s.Client, s.Destination, s.Broadcast, found, window.Token)).ToList();
            foreach (var (client, destination, _) in sockets)
            {
                // Twice: a datagram can be lost, and a server that is slow to wake answers the second one.
                await client.SendAsync(Probe, destination, cancellationToken).ConfigureAwait(false);
                await client.SendAsync(Probe, destination, cancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAll(receivers).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (client, _, _) in sockets)
            {
                client.Dispose();
            }
        }

        lock (found)
        {
            return [.. found];
        }
    }

    private async Task ReceiveAsync(UdpClient client, IPEndPoint destination, bool broadcast, List<(IPAddress, int)> found, CancellationToken window)
    {
        while (!window.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(window).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // e.g. the destination refused: nothing listens there
            }

            // A named host answers for itself only; a broadcast takes whoever answers.
            if (!broadcast && !received.RemoteEndPoint.Address.Equals(destination.Address))
            {
                continue;
            }

            if (TryReadPort(received.Buffer, out var port))
            {
                lock (found)
                {
                    if (!found.Contains((received.RemoteEndPoint.Address, port)))
                    {
                        found.Add((received.RemoteEndPoint.Address, port));
                    }
                }
            }
        }
    }

    private static bool TryReadPort(byte[] datagram, out int port)
    {
        port = 0;
        try
        {
            using var document = JsonDocument.Parse(datagram);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("AlpacaPort", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out port))
                {
                    return port is >= 1 and <= 65535;
                }
            }
        }
        catch (JsonException)
        {
            // Something else answered on that port: not an Alpaca server.
        }

        return false;
    }

    private async Task<AlpacaServer> DescribeAsync(string host, int port, CancellationToken cancellationToken)
    {
        var endpoint = new AlpacaEndpoint(host, port);
        using var transport = new AlpacaTransport(endpoint.ServerUri, httpFactory?.Invoke(), TimeSpan.FromSeconds(4));
        var devices = new List<AlpacaDeviceInfo>();
        var list = await transport.GetAsync("management/v1/configureddevices", cancellationToken).ConfigureAwait(false);
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                string? Text(string name) => item.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
                var number = item.EnumerateObject().FirstOrDefault(p => p.Name.Equals("DeviceNumber", StringComparison.OrdinalIgnoreCase)).Value;
                if (Text("DeviceType") is { } type)
                {
                    devices.Add(new AlpacaDeviceInfo(Text("DeviceName") ?? type, type, number.ValueKind == JsonValueKind.Number ? number.GetInt32() : 0, Text("UniqueID")));
                }
            }
        }

        string? name = null;
        try
        {
            var description = await transport.GetAsync("management/v1/description", cancellationToken).ConfigureAwait(false);
            if (description.ValueKind == JsonValueKind.Object)
            {
                name = description.EnumerateObject().FirstOrDefault(p => p.Name.Equals("ServerName", StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is AlpacaCommunicationException or AlpacaException)
        {
            _logger.LogDebug(ex, "The Alpaca server at {Host}:{Port} has no description", host, port);
        }

        return new AlpacaServer(host, port, name, devices);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var text = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(text, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(text, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // IPv4 first: that is where Alpaca discovery answers in practice; an IPv6 address only when there is no other.
        var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        return v4.Length > 0 ? v4 : addresses;
    }

    private static IEnumerable<(IPAddress Local, IPAddress Broadcast)> BroadcastAddresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback || !network.SupportsMulticast)
            {
                continue;
            }

            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    broadcast[i] = (byte)(address[i] | ~mask[i]);
                }

                yield return (unicast.Address, new IPAddress(broadcast));
            }
        }
    }
}
