using Sidera.Core.Devices;

namespace Sidera.Runtime.Devices;

/// <summary>Where the device an instruction uses came from.</summary>
public enum DeviceResolutionSource
{
    /// <summary>The instruction names the device.</summary>
    Explicit,

    /// <summary>The imaging setup of the instruction binds the device.</summary>
    ImagingSetup,

    /// <summary>Nothing names it and there is exactly one device of the kind that can be meant.</summary>
    OnlyDevice,

    /// <summary>There is no device of the kind.</summary>
    None,

    /// <summary>There are several and nothing says which: the instruction is not valid until somebody chooses.</summary>
    Ambiguous
}

/// <summary>The answer to "which device does this instruction use": the device and where it came from, or why there is none.</summary>
public sealed record DeviceResolution(DeviceResolutionSource Source, DeviceId? Device, IReadOnlyList<DeviceId> Candidates, string Kind)
{
    public bool IsResolved => Device is not null;

    /// <summary>The sentence that says why no device was found; <c>null</c> when one was.</summary>
    public string? Problem => Source switch
    {
        DeviceResolutionSource.None => $"There is no {Kind}. Add one on the Equipment page.",
        DeviceResolutionSource.Ambiguous => $"There are several {Kind}s ({string.Join(", ", Candidates.Select(c => c.Value))}): choose which one to use.",
        _ => null,
    };
}

/// <summary>
/// Resolves the device an instruction uses, in this order and no other: the device the instruction names, else the device that its imaging setup binds, else the one device of the kind that can be
/// meant, else a problem. "The one device that can be meant" is the only device of the kind that is connected, or, when none is connected, the only one that is configured; two or more are never
/// resolved by picking one (not the first, not the one with the lowest id).
/// </summary>
public static class DeviceResolver
{
    public static DeviceResolution Resolve<T>(DeviceRegistry registry, DeviceId? explicitDevice, DeviceId? setupBinding, string kind) where T : class, IDevice
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (explicitDevice is { } named)
        {
            return new DeviceResolution(DeviceResolutionSource.Explicit, named, [named], kind);
        }

        if (setupBinding is { } bound)
        {
            return new DeviceResolution(DeviceResolutionSource.ImagingSetup, bound, [bound], kind);
        }

        var all = registry.GetAll().OfType<T>().ToList();
        var connected = all.Where(d => d.ConnectionState == DeviceConnectionState.Connected).ToList();
        var candidates = (connected.Count > 0 ? connected : all).Select(d => d.Id).OrderBy(id => id.Value, StringComparer.Ordinal).ToList();
        return candidates.Count switch
        {
            0 => new DeviceResolution(DeviceResolutionSource.None, null, [], kind),
            1 => new DeviceResolution(DeviceResolutionSource.OnlyDevice, candidates[0], candidates, kind),
            _ => new DeviceResolution(DeviceResolutionSource.Ambiguous, null, candidates, kind),
        };
    }
}
