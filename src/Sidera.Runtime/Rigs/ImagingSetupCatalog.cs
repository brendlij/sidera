using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Rigs;

/// <summary>Where the sequencer looks for imaging setups: the registered ones, and the one that is implied when there is only one camera.</summary>
public interface ISetupSource
{
    IReadOnlyCollection<Rig> GetAll();

    bool TryGet(RigId id, out Rig? rig);
}

/// <summary>
/// The imaging setups of a session: the ones that were configured (the <see cref="RigRegistry"/>) and, for somebody who has one camera and has configured no setup, an <b>implicit</b> one made
/// of the devices there are. The implicit setup is computed, never stored and never listed as something the user made; it exists so that a single-camera user does not have to build a setup
/// before the workflow can image. It exists only where nothing has to be guessed: one camera that is available (connected, or when none is connected, configured) and not in a configured setup; each
/// other kind of device is part of it only when there is exactly one available. Two cameras and no setup: no implicit setup, and the workflow says that a setup has to be chosen or made.
/// <para>
/// A setup is <b>usable</b> when its camera is connected. With some setups usable, only those count (a second camera that is configured and not connected is no reason to show multi-device
/// controls); with none connected, all that are configured count, so that a session can be planned offline.
/// </para>
/// </summary>
public sealed class ImagingSetupCatalog(DeviceRegistry devices, RigRegistry rigs) : ISetupSource
{
    /// <summary>The id of the implicit setup. It never appears in a saved sequence as a choice: a block that says "Auto" means the only usable setup.</summary>
    public static readonly RigId ImplicitId = new("setup.implicit");

    public IReadOnlyCollection<Rig> GetAll()
    {
        var all = rigs.GetAll().ToList();
        return ImplicitSetup(all) is { } implied ? [.. all, implied] : all;
    }

    public bool TryGet(RigId id, out Rig? rig)
    {
        if (rigs.TryGet(id, out rig))
        {
            return true;
        }

        if (id == ImplicitId && ImplicitSetup([.. rigs.GetAll()]) is { } implied)
        {
            rig = implied;
            return true;
        }

        rig = null;
        return false;
    }

    /// <summary>Whether the setup can image now: its camera is there and connected.</summary>
    public bool IsUsable(Rig rig) => devices.TryGet(rig.CameraId, out var camera) && camera is { ConnectionState: DeviceConnectionState.Connected };

    /// <summary>The setups that count: the ones whose camera is connected; when none is, all of them.</summary>
    public IReadOnlyList<Rig> UsableSetups()
    {
        var all = GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var connected = all.Where(IsUsable).ToList();
        return connected.Count > 0 ? connected : all;
    }

    /// <summary>Whether there is more than one setup to image with, so that choosing between them, running them side by side and what they share is something to show.</summary>
    public bool IsMultiSetup => UsableSetups().Count >= 2;

    // ---- the implicit setup

    private Rig? ImplicitSetup(IReadOnlyList<Rig> configured)
    {
        var camera = OnlyAvailable<ICamera>();
        if (camera is null || configured.Any(r => r.CameraId == camera.Id))
        {
            return null;
        }

        return new Rig(
            ImplicitId, camera.Name, camera.Id, optics: null,
            focuserId: OnlyAvailable<IFocuser>()?.Id, filterWheelId: OnlyAvailable<IFilterWheel>()?.Id, rotatorId: null, rotatorModel: null,
            mountId: OnlyAvailable<IMount>()?.Id, guiderId: OnlyAvailable<IGuider>()?.Id);
    }

    // The one device of the kind that can be meant: the only one that is connected, or, when none is, the only one there is. None when there are several (nothing is guessed) or none.
    private T? OnlyAvailable<T>() where T : class, IDevice
    {
        var all = devices.GetAll().OfType<T>().ToList();
        var connected = all.Where(d => d.ConnectionState == DeviceConnectionState.Connected).ToList();
        var candidates = connected.Count > 0 ? connected : all;
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
