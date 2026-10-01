using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Runtime;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// Everything registered with the runtime, as cards: the rigs, and a card for each camera, mount and guider.
/// Other device kinds are not shown because Astra has nothing to do with them yet.
/// </summary>
public sealed class EquipmentViewModel : ViewModelBase, IDisposable
{
    public EquipmentViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        TimeSpan manualExposure
    )
    {
        var devices = host.DeviceRegistry.GetAll().OrderBy(d => d.Id.Value, StringComparer.Ordinal).ToList();

        Cameras = devices.OfType<ICamera>()
            .Select(c => new CameraViewModel(c, host, postToUi, activity, imaging, manualExposure)).ToList();
        Mounts = devices.OfType<IMount>().Select(m => new MountViewModel(m, host, postToUi, activity)).ToList();
        Guiders = devices.OfType<IGuider>().Select(g => new GuiderViewModel(g, host, postToUi, activity)).ToList();
        Rigs = host.RigRegistry.GetAll().OrderBy(r => r.Id.Value, StringComparer.Ordinal)
            .Select(r => new RigViewModel(r, host, Cameras)).ToList();
    }

    public IReadOnlyList<RigViewModel> Rigs { get; }
    public IReadOnlyList<CameraViewModel> Cameras { get; }
    public IReadOnlyList<MountViewModel> Mounts { get; }
    public IReadOnlyList<GuiderViewModel> Guiders { get; }

    public bool HasRigs => Rigs.Count > 0;

    /// <summary>Every device card, in display order.</summary>
    public IEnumerable<DeviceViewModelBase> Devices => Cameras.Cast<DeviceViewModelBase>().Concat(Mounts).Concat(Guiders);

    public void Dispose()
    {
        foreach (var device in Devices)
        {
            device.Dispose();
        }
    }
}
