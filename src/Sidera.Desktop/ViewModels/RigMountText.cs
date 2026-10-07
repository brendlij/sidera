using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.ViewModels;

/// <summary>Says which mount a rig-local step works with: the mount of its rig. Shown in the editor, never edited there.</summary>
internal static class RigMountText
{
    public static string Of(ISetupSource? rigs, DeviceRegistry devices, RigId? rigId, DeviceId? named, SharedEquipmentDraft? shared)
    {
        if (rigId is not { } id || rigs is null || !rigs.TryGet(id, out var rig) || rig is null)
        {
            return "Select an imaging setup.";
        }

        var mount = StepScopes.EffectiveMount(rig, named, shared);
        if (mount is not { } mountId)
        {
            return "The imaging setup has no mount. Give it one on the Equipment page.";
        }

        var name = devices.TryGet(mountId, out var device) && device is not null ? device.Name : mountId.Value;
        return rig.MountId is not null ? name + " · the mount of the imaging setup" : name + " · named by the step";
    }
}
