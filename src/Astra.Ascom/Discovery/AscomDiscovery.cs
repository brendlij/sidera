using ASCOM.Com;
using ASCOM.Common;
using Astra.Core.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Ascom.Discovery;

/// <summary>The kinds of ASCOM device Astra can drive. ASCOM calls a mount a telescope.</summary>
public enum AscomDeviceKind
{
    Camera,
    Mount,
    Focuser,
}

public static class AscomDeviceKinds
{
    /// <summary>The ASCOM kind of an Astra device type, or <c>null</c> when Astra has no ASCOM adapter for it.</summary>
    public static AscomDeviceKind? From(DeviceType type) => type switch
    {
        DeviceType.Camera => AscomDeviceKind.Camera,
        DeviceType.Mount => AscomDeviceKind.Mount,
        DeviceType.Focuser => AscomDeviceKind.Focuser,
        _ => null,
    };

    public static DeviceType ToDeviceType(this AscomDeviceKind kind) => kind switch
    {
        AscomDeviceKind.Camera => DeviceType.Camera,
        AscomDeviceKind.Mount => DeviceType.Mount,
        _ => DeviceType.Focuser,
    };
}

/// <summary>A driver registered with ASCOM. The ProgId is its stable identifier: it is what Astra stores.</summary>
public sealed record AscomDriverInfo(string ProgId, string Name);

/// <param name="PlatformAvailable">ASCOM Platform is installed and could be asked.</param>
/// <param name="Drivers">The registered drivers of the kind, by name; empty when there are none or the platform is missing.</param>
/// <param name="Problem">What went wrong, in a sentence for the user; <c>null</c> when discovery worked (even with no driver).</param>
public sealed record AscomDiscoveryResult(bool PlatformAvailable, IReadOnlyList<AscomDriverInfo> Drivers, string? Problem);

/// <summary>Finds the ASCOM drivers installed on this machine.</summary>
public interface IAscomDiscovery
{
    Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovery through the ASCOM.Com library (<c>PlatformUtilities</c> and <c>Profile</c>), the path that works on
/// .NET 10; the registry is not read here. It only reads the profile store: no driver is created and no COM object is
/// touched, so it does not need the dispatcher. Failures are reported in the result, never thrown.
/// </summary>
public sealed class AscomDiscovery(ILogger? logger = null) : IAscomDiscovery
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default) =>
        // The one place that moves work to the thread pool: reading the profile store can take a moment.
        Task.Run(() => Discover(kind), cancellationToken);

    private AscomDiscoveryResult Discover(AscomDeviceKind kind)
    {
        try
        {
            if (!PlatformUtilities.IsPlatformInstalled())
            {
                _logger.LogWarning("ASCOM discovery: the ASCOM Platform is not installed");
                return new AscomDiscoveryResult(
                    false, [], "ASCOM Platform is not installed on this computer. Install it from ascom-standards.org to use ASCOM drivers.");
            }

            var type = kind switch
            {
                AscomDeviceKind.Camera => DeviceTypes.Camera,
                AscomDeviceKind.Mount => DeviceTypes.Telescope,
                _ => DeviceTypes.Focuser,
            };

            var drivers = Profile.GetDrivers(type)
                .Where(d => !string.IsNullOrWhiteSpace(d.ProgID))
                .Select(d => new AscomDriverInfo(d.ProgID, string.IsNullOrWhiteSpace(d.Name) ? d.ProgID : d.Name))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.ProgId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation("ASCOM discovery: {Count} {Kind} driver(s) found", drivers.Count, kind);
            return new AscomDiscoveryResult(true, drivers, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ASCOM discovery of {Kind} drivers failed", kind);
            return new AscomDiscoveryResult(false, [], $"The ASCOM drivers could not be listed: {ex.Message.Split('\n', 2)[0].Trim()}");
        }
    }
}
