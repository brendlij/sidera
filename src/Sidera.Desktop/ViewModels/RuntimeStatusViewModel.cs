using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Coordination;
using Sidera.Core.Resources;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// A small read-only look at the runtime: how much equipment is registered, which devices are in use right now and
/// what the safe-point coordination is doing. It only reads what the public runtime APIs offer; the resource manager
/// does not publish how many operations are waiting, so that is not shown.
/// </summary>
public sealed partial class RuntimeStatusViewModel : ViewModelBase
{
    private readonly SideraRuntimeHost _host;
    private readonly IReadOnlyList<CoordinationGroupId> _groups;

    public RuntimeStatusViewModel(SideraRuntimeHost host, IReadOnlyList<CoordinationGroupId> coordinationGroups)
    {
        _host = host;
        _groups = coordinationGroups;
        Refresh();
    }

    /// <summary>Sidera runs on this computer only.</summary>
    public string ModeText => "Local runtime";

    /// <summary>The runtime is part of the application: it is there for as long as the window is.</summary>
    public string StatusText => "Runtime online";

    [ObservableProperty]
    public partial int DeviceCount { get; private set; }

    [ObservableProperty]
    public partial int RigCount { get; private set; }

    /// <summary>The devices an operation holds exclusively right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeldCount))]
    [NotifyPropertyChangedFor(nameof(HeldText))]
    public partial IReadOnlyList<string> HeldDevices { get; private set; } = [];

    public int HeldCount => HeldDevices.Count;

    public string HeldText => HeldDevices.Count == 0 ? "None in use" : string.Join(", ", HeldDevices);

    /// <summary>One sentence per coordination group that has participants; empty when nothing is coordinated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinationText))]
    public partial IReadOnlyList<string> Coordination { get; private set; } = [];

    public string CoordinationText => Coordination.Count == 0 ? "No branches coordinated" : string.Join(" · ", Coordination);

    /// <summary>Reads the runtime again; call on the UI thread.</summary>
    public void Refresh()
    {
        var devices = _host.DeviceRegistry.GetAll();
        DeviceCount = devices.Count;
        RigCount = _host.RigRegistry.GetAll().Count;

        var held = devices
            .Where(d => _host.ResourceManager.IsHeld(ResourceId.ForDevice(d.Id)))
            .Select(d => d.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!held.SequenceEqual(HeldDevices))
        {
            HeldDevices = held;
        }

        var coordination = new List<string>();
        foreach (var group in _groups)
        {
            var status = _host.SafePointCoordinator.GetStatus(group);
            if (status.Participants.Count == 0)
            {
                continue;
            }

            var atSafePoint = status.AtSafePoint.Count;
            var total = status.Participants.Count;
            coordination.Add(
                status.OperationRunning ? $"Coordinated operation running · {atSafePoint} of {total} branches held at a safe point"
                : status.RequestPending ? $"Coordinated operation waiting · {atSafePoint} of {total} branches at a safe point"
                : $"{total} branches coordinated, no operation pending");
        }

        if (!coordination.SequenceEqual(Coordination))
        {
            Coordination = coordination;
        }
    }
}
