using System.Globalization;

namespace Sidera.Alpaca;

/// <summary>
/// Where an Alpaca device is: the host (a name or an address), the port of the Alpaca server and the number of the device on it. Alpaca is plain HTTP on the local network, so there is no scheme.
/// </summary>
public sealed record AlpacaEndpoint
{
    public AlpacaEndpoint(string host, int port, int deviceNumber = 0)
    {
        Host = Normalize(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfNegative(deviceNumber);
        Port = port;
        DeviceNumber = deviceNumber;
    }

    public string Host { get; }

    public int Port { get; }

    public int DeviceNumber { get; }

    /// <summary>The server: <c>http://host:port/</c>.</summary>
    public Uri ServerUri => new($"http://{HostForUri}:{Port.ToString(CultureInfo.InvariantCulture)}/");

    public const string HostKey = "host";
    public const string PortKey = "port";
    public const string DeviceNumberKey = "deviceNumber";

    /// <summary>The endpoint of the settings of a device (<c>host</c>, <c>port</c>, <c>deviceNumber</c>); there is no default address, so a missing host or port is an error.</summary>
    /// <exception cref="FormatException">A setting is missing or is not valid.</exception>
    public static AlpacaEndpoint FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TryGetValue(HostKey, out var host) || string.IsNullOrWhiteSpace(host))
        {
            throw new FormatException("The Alpaca device has no host.");
        }

        if (!settings.TryGetValue(PortKey, out var portText) || !int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException("The port of the Alpaca server must be a number from 1 to 65535.");
        }

        var number = 0;
        if (settings.TryGetValue(DeviceNumberKey, out var numberText) && !string.IsNullOrWhiteSpace(numberText)
            && (!int.TryParse(numberText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0))
        {
            throw new FormatException("The device number of the Alpaca device must be a whole number, 0 or more.");
        }

        try
        {
            return new AlpacaEndpoint(host, port, number);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    /// <summary>The settings that store this endpoint.</summary>
    public IReadOnlyDictionary<string, string> ToSettings() => new Dictionary<string, string>
    {
        [HostKey] = Host,
        [PortKey] = Port.ToString(CultureInfo.InvariantCulture),
        [DeviceNumberKey] = DeviceNumber.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The way it is written and stored: <c>host:port/number</c> (an IPv6 address in brackets).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{HostForUri}:{Port}/{DeviceNumber}");

    private string HostForUri => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;

    /// <summary>Reads <c>host:port</c> or <c>host:port/number</c>, with or without <c>http://</c>; the number is 0 when it is left out, the port 80 when no port is given.</summary>
    public static bool TryParse(string? text, out AlpacaEndpoint? endpoint, out string? problem)
    {
        endpoint = null;
        problem = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            problem = "Enter the address of the Alpaca server, like 192.168.1.20:11111.";
            return false;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || string.IsNullOrEmpty(uri.Host))
        {
            problem = "The address must look like host:port or host:port/number, for example 192.168.1.20:11111/0.";
            return false;
        }

        var number = 0;
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length > 0 && (!int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0))
        {
            problem = "The device number after the slash must be a whole number, 0 or more.";
            return false;
        }

        if (uri.Port is < 1 or > 65535)
        {
            problem = "The port must be between 1 and 65535.";
            return false;
        }

        endpoint = new AlpacaEndpoint(uri.Host.Trim('[', ']'), uri.Port, number);
        return true;
    }

    private static string Normalize(string host)
    {
        var trimmed = (host ?? string.Empty).Trim().Trim('[', ']');
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '?' or '#' or '@'))
        {
            throw new ArgumentException("The host must be a name or an address.", nameof(host));
        }

        return trimmed;
    }
}
