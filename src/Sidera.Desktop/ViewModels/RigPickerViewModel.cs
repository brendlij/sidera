using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>A rig a Rig Track can use: its name, its id, and its camera.</summary>
/// <param name="IsMissing">The id was selected once but no rig with it is registered any more.</param>
public sealed record RigOption(RigId Id, string Name, string CameraText, bool IsMissing = false)
{
    public string IdText => Id.Value;

    /// <summary>What is shown under the name: the camera of the setup, or, for the setup that a single camera is on its own, that it is one. Never the id.</summary>
    public string DetailText => IsMissing ? string.Empty : Id.Value.StartsWith("setup.implicit", StringComparison.Ordinal) ? "The camera on its own, no imaging setup made" : CameraText;

    public bool HasDetail => !IsMissing;
}

/// <summary>
/// Picks one rig from the <see cref="ISetupSource"/>. A selected rig that is not registered (any more) stays selected,
/// shown as missing, so the user can see what the track refers to; the validation reports it, and it is never
/// silently replaced.
/// </summary>
public sealed partial class RigPickerViewModel : ObservableObject
{
    private readonly ISetupSource? _rigs;
    private readonly DeviceRegistry _devices;
    private bool _refreshing;

    public RigPickerViewModel(ISetupSource? rigs, DeviceRegistry devices, RigId? initial)
    {
        _rigs = rigs;
        _devices = devices;
        Options = BuildOptions(initial);
        _refreshing = true;
        Selected = initial is { } id ? Options.First(o => o.Id == id) : null;
        _refreshing = false;
    }

    /// <summary>When set, only these rigs are offered (and a selected rig that is not among them shows as missing).</summary>
    public Func<IReadOnlySet<RigId>>? LimitTo { get; set; }

    public IReadOnlyList<RigOption> Options { get; private set; }

    [ObservableProperty]
    public partial RigOption? Selected { get; set; }

    public RigId? SelectedId => Selected?.Id;

    /// <summary>Raised when another rig is picked; not when the list of options is refreshed or reset.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the registry again: rigs that appeared are offered, a rig that disappeared is marked missing.</summary>
    public void Refresh() => Apply(SelectedId);

    /// <summary>Selects <paramref name="id"/> without it counting as a choice of the user.</summary>
    public void Reset(RigId? id) => Apply(id, force: true);

    private void Apply(RigId? selected, bool force = false)
    {
        var options = BuildOptions(selected);
        if (!force && options.SequenceEqual(Options))
        {
            return;
        }

        _refreshing = true;
        try
        {
            Options = options;
            OnPropertyChanged(nameof(Options));
            Selected = selected is { } id ? options.First(o => o.Id == id) : null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedChanged(RigOption? value)
    {
        if (!_refreshing)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<RigOption> BuildOptions(RigId? selected)
    {
        var limit = LimitTo?.Invoke();
        var options = (_rigs?.GetAll() ?? [])
            .Where(rig => limit is null || limit.Contains(rig.Id))
            .OrderBy(rig => rig.Id.Value, StringComparer.Ordinal)
            .Select(rig => new RigOption(rig.Id, rig.Name, CameraName(rig.CameraId)))
            .ToList();

        if (selected is { } id && options.All(o => o.Id != id))
        {
            options.Insert(0, new RigOption(id, $"{id.Value} (not available)", string.Empty, IsMissing: true));
        }

        return options;
    }

    private string CameraName(DeviceId id) =>
        _devices.TryGet(id, out var device) && device is not null ? device.Name : id.Value;
}
