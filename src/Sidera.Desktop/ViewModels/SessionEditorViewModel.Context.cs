using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.ViewModels;

/// <summary>An imaging setup as a choice; <see cref="Id"/> is <c>null</c> for "the only setup".</summary>
public sealed record SetupChoice(ImagingBindingId? Id, string Name, string Detail)
{
    public bool IsAuto => Id is null;
}

/// <summary>A filter of a setup's wheel as a choice; <see cref="Slot"/> is <c>null</c> for "no filter change".</summary>
public sealed record FilterChoice(int? Slot, string Name);

public sealed partial class SessionEditorViewModel
{
    // ---- the setups

    private IReadOnlyList<Rig> Rigs => _rigs?.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    // The setups that can image now: the ones whose camera is connected (when none is, all of them). A setup that is configured and not connected is not a reason to ask which one is meant.
    internal IReadOnlyList<Rig> UsableRigs => _rigs is ImagingSetupCatalog catalog ? catalog.UsableSetups() : Rigs;

    private IReadOnlySet<RigId>? UsableIds => _rigs is ImagingSetupCatalog ? UsableRigs.Select(r => r.Id).ToHashSet() : null;

    /// <summary>
    /// There is more than one setup to image with. Only then is anything about choosing a setup, running setups side by side or what they share shown: with one, the session is a start, targets
    /// with their blocks, and an end, and nothing else.
    /// </summary>
    public bool IsMultiSetup => UsableRigs.Count >= 2;

    /// <summary>The imaging setup the application works with now (the switcher in the sidebar): a new target starts with its sequence when several setups can image. Set by the application.</summary>
    public Func<RigId?>? CurrentSetup { get; set; }

    /// <summary>The imaging path of a setup: what a sequence is bound to.</summary>
    internal static ImagingBindingId PathOf(Rig rig) => ImagingBindingId.Of(rig);

    /// <summary>The setup a binding means: the one it names; for "the only setup", the one there is (when there is exactly one). Never a guess between several.</summary>
    internal Rig? ResolvedSetup(ImagingBindingId? binding) =>
        binding is { } named ? (_rigs is not null && _rigs.TryResolve(named, out var rig) ? rig : null) : UsableRigs.Count == 1 ? UsableRigs[0] : null;

    /// <summary>The names of the filters of the wheel of a setup, by slot; <c>null</c> for a slot that is not there or a setup without a wheel.</summary>
    internal Func<int, string?> FilterNameOf(ImagingBindingId? binding)
    {
        var rig = ResolvedSetup(binding);
        return slot => rig?.FilterWheelId is { } id && _registry.TryGet(id, out var device) && device is IFilterWheel wheel && slot >= 0 && slot < wheel.Slots.Count ? wheel.Slots[slot].Name : null;
    }

    /// <summary>The filters of a setup for a choice: no change first, then the wheel's own.</summary>
    internal IReadOnlyList<FilterChoice> FiltersOf(Rig? rig)
    {
        var list = new List<FilterChoice> { new(null, "No filter change") };
        if (rig?.FilterWheelId is { } id && _registry.TryGet(id, out var device) && device is IFilterWheel wheel)
        {
            list.AddRange(wheel.Slots.Select(slot => new FilterChoice(slot.Index, slot.Name)));
        }

        return list;
    }

    /// <summary>The setups a sequence can be for, as choices (with what each is made of in a few words).</summary>
    internal IReadOnlyList<SetupChoice> SetupChoices() => UsableRigs.Select(rig => new SetupChoice(PathOf(rig), rig.Name, DescribeSetup(rig))).ToList();

    private string DescribeSetup(Rig rig)
    {
        var camera = _registry.TryGet(rig.CameraId, out var device) ? device!.Name : "no camera";
        var optics = rig.Optics is { } o ? $" · {o.FocalLengthMm:0.#} mm" : string.Empty;
        var state = _rigs is ImagingSetupCatalog catalog && !catalog.IsUsable(rig) ? " · not connected" : string.Empty;
        return camera + optics + state;
    }

    private HashSet<RigId>? _lastUsable;

    /// <summary>Looks at which setups can image now (a camera was connected or disconnected) and, when that changed, shows the setups again. Called about once a second with the other refreshes.</summary>
    public void RefreshAvailability()
    {
        var now = UsableRigs.Select(r => r.Id).ToHashSet();
        if (_lastUsable is not null && _lastUsable.SetEquals(now))
        {
            return;
        }

        RefreshSetups();
        _lastUsable = now;
    }

    /// <summary>Reads the setups again (one came, went or changed) and shows the session with them.</summary>
    public void RefreshSetups()
    {
        if (Session is not null)
        {
            Refresh(modified: false);
        }

        OnPropertyChanged(nameof(IsMultiSetup));
        OnPropertyChanged(nameof(CanAddSequence));
    }

    /// <summary>"Main 750mm + Wide 400mm" for the setups that image together under a target; empty with one.</summary>
    internal string ParallelNamesOf(SessionTarget target)
    {
        var names = target.Lanes.Where(l => l.Blocks.Any(b => b.Enabled)).Select(l => ResolvedSetup(l.Setup)?.Name).OfType<string>().ToList();
        return names.Count >= 2 ? string.Join(" + ", names) : string.Empty;
    }

    /// <summary>The devices that setups of one target share, with the setups: "AM3 · PHD2". Empty with one setup or none shared.</summary>
    internal string SharedNamesOf(SessionTarget target)
    {
        var rigs = target.Lanes.Where(l => l.Blocks.Any(b => b.Enabled)).Select(l => ResolvedSetup(l.Setup)).OfType<Rig>().DistinctBy(r => r.Id).ToList();
        if (rigs.Count < 2)
        {
            return string.Empty;
        }

        var shared = new List<string>();
        void Group(Func<Rig, DeviceId?> of)
        {
            foreach (var group in rigs.Where(r => of(r) is not null).GroupBy(r => of(r)!.Value).Where(g => g.Count() >= 2))
            {
                shared.Add(_registry.TryGet(group.Key, out var device) ? device!.Name : group.Key.Value);
            }
        }

        Group(r => r.MountId);
        Group(r => r.GuiderId);
        return string.Join(" · ", shared);
    }

    // ---- what can be added

    /// <summary>A sequence for another setup can be added under a target: there is a setup that is not used by it yet.</summary>
    public bool CanAddSequence => IsEditable && IsMultiSetup;

    /// <summary>Set when a sequence was asked for and there is no other imaging setup to give it: it says so and offers to make one.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSetupNotice))]
    public partial string SetupNotice { get; private set; } = string.Empty;

    public bool HasSetupNotice => SetupNotice.Length > 0;

    /// <summary>Goes to the Equipment page to make an imaging setup: what the notice offers.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CreateImagingSetup() => _openEquipment?.Invoke();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissSetupNotice() => SetupNotice = string.Empty;
}
