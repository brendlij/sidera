using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Runtime.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>A device a user can pick: its friendly name, with the id as secondary text.</summary>
/// <param name="IsMissing">The id was chosen once but no registered device of the right kind has it any more.</param>
public sealed record DeviceOption(DeviceId Id, string Name, bool IsMissing = false)
{
    public string IdText => Id.Value;
    public bool HasIdText => !IsMissing;
}

/// <summary>
/// Picks one device of a kind from the <see cref="DeviceRegistry"/>. A selected device that is not registered (any
/// more) stays selected, shown as missing, so the user can see what the step refers to; the validation reports it,
/// and it is never silently replaced.
/// </summary>
public sealed partial class DevicePickerViewModel : ObservableObject
{
    private readonly DeviceRegistry _registry;
    private readonly Func<IDevice, bool> _accepts;
    private bool _refreshing;

    /// <param name="accepts">Whether a registered device is of the kind this picker offers.</param>
    public DevicePickerViewModel(DeviceRegistry registry, Func<IDevice, bool> accepts, DeviceId? initial)
    {
        _registry = registry;
        _accepts = accepts;
        _refreshing = true;
        Options = BuildOptions(initial);
        Selected = initial is { } id ? Options.First(o => o.Id == id) : null;
        _refreshing = false;
    }

    public IReadOnlyList<DeviceOption> Options { get; private set; }

    [ObservableProperty]
    public partial DeviceOption? Selected { get; set; }

    public DeviceId? SelectedId => Selected?.Id;

    /// <summary>Raised when another device is picked; not when the list of options is refreshed.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the registry again: devices that appeared are offered, devices that disappeared are marked missing.</summary>
    public void Refresh()
    {
        var selectedId = SelectedId;
        var options = BuildOptions(selectedId);
        if (options.SequenceEqual(Options))
        {
            return;
        }

        // Replacing the options makes a bound ComboBox reset its selection; that is not a choice of the user.
        _refreshing = true;
        try
        {
            Options = options;
            OnPropertyChanged(nameof(Options));
            Selected = selectedId is { } id ? options.First(o => o.Id == id) : null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Selects <paramref name="id"/> without it counting as a choice of the user (used when a document is opened).</summary>
    public void Reset(DeviceId? id)
    {
        _refreshing = true;
        try
        {
            Options = BuildOptions(id);
            OnPropertyChanged(nameof(Options));
            Selected = id is { } selected ? Options.First(o => o.Id == selected) : null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedChanged(DeviceOption? value)
    {
        if (!_refreshing)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<DeviceOption> BuildOptions(DeviceId? selected)
    {
        var options = _registry.GetAll()
            .Where(_accepts)
            .OrderBy(d => d.Id.Value, StringComparer.Ordinal)
            .Select(d => new DeviceOption(d.Id, d.Name))
            .ToList();

        if (selected is { } id && options.All(o => o.Id != id))
        {
            options.Insert(0, new DeviceOption(id, $"{id.Value} (not available)", IsMissing: true));
        }

        return options;
    }
}
