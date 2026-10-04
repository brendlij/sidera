using System.Globalization;

namespace Sidera.Phd2;

/// <summary>
/// Where the event server of PHD2 listens. PHD2 uses port 4400, and 4401, 4402 ... for further running instances. Both parts are settings of
/// the guider device, and nothing else of PHD2 is configured in Sidera.
/// </summary>
public sealed record Phd2Endpoint(string Host, int Port)
{
    public const string HostKey = "host";
    public const string PortKey = "port";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 4400;

    public static Phd2Endpoint Default { get; } = new(DefaultHost, DefaultPort);

    /// <summary>"127.0.0.1:4400".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");

    /// <summary>A sentence about what is wrong with the endpoint, or <c>null</c> when it can be used.</summary>
    public string? Problem() => Problem(Host, Port);

    public static string? Problem(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "The host of PHD2 must not be empty.";
        }

        return port is < 1 or > 65535 ? "The port of PHD2 must be from 1 to 65535." : null;
    }

    /// <summary>The endpoint of the settings of a device; a missing host or port is the default, one that is not valid is an error.</summary>
    /// <exception cref="FormatException">The port is not a number, or the endpoint is not valid.</exception>
    public static Phd2Endpoint FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var host = settings.TryGetValue(HostKey, out var h) && !string.IsNullOrWhiteSpace(h) ? h.Trim() : DefaultHost;
        var port = DefaultPort;
        if (settings.TryGetValue(PortKey, out var p) && !string.IsNullOrWhiteSpace(p)
            && !int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            throw new FormatException("The port of PHD2 must be a number from 1 to 65535.");
        }

        return Problem(host, port) is { } problem ? throw new FormatException(problem) : new Phd2Endpoint(host, port);
    }

    /// <summary>The settings that store this endpoint.</summary>
    public IReadOnlyDictionary<string, string> ToSettings() =>
        new Dictionary<string, string> { [HostKey] = Host, [PortKey] = Port.ToString(CultureInfo.InvariantCulture) };
}
