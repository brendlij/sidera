using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.FilterWheels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>A slot of a filter wheel as a user picks it: the name first, the slot as secondary text.</summary>
/// <param name="IsMissing">The slot was chosen once but the wheel has no such slot (or no wheel is known).</param>
public sealed record FilterOption(int Index, string Name, bool IsMissing = false)
{
    public string DisplayName => IsMissing ? $"Slot {Index} (not available)" : Name;
    public string DetailText => IsMissing ? string.Empty : $"slot {Index}";
    public bool HasDetail => !IsMissing;
}

/// <summary>
/// Picks one slot of a filter wheel by its name. What is kept is the index of the slot: that is what the step stores
/// and what the wheel is told. The names come from the wheel that the owner of this choice currently has (the wheel
/// a step selected, or the wheel of the rig of a track) and are looked up again whenever that can have changed. An
/// index that the wheel does not have stays selected, shown as missing, and the validation reports it: it is never
/// replaced by another slot.
/// </summary>
public sealed partial class FilterChoiceViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<FilterSlot>?> _slots;
    private bool _refreshing;
    private int _index;

    /// <param name="slots">The slots of the wheel there is now, or <c>null</c> when there is none.</param>
    public FilterChoiceViewModel(Func<IReadOnlyList<FilterSlot>?> slots, int initialIndex)
    {
        _slots = slots;
        _index = initialIndex;
        Options = BuildOptions();
        _refreshing = true;
        Selected = Options.First(o => o.Index == _index);
        _refreshing = false;
    }

    public IReadOnlyList<FilterOption> Options { get; private set; }

    [ObservableProperty]
    public partial FilterOption? Selected { get; set; }

    /// <summary>The index of the chosen slot: the choice, also while the wheel has no such slot.</summary>
    public int SelectedIndex => _index;

    /// <summary>Raised when another slot is picked; not when the names are looked up again.</summary>
    public event EventHandler? Changed;

    /// <summary>Looks the slots up again (the wheel may have changed), keeping the chosen index.</summary>
    public void Refresh()
    {
        var options = BuildOptions();
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
            Selected = options.First(o => o.Index == _index);
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedChanged(FilterOption? value)
    {
        if (_refreshing || value is null)
        {
            return;
        }

        _index = value.Index;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<FilterOption> BuildOptions()
    {
        var options = (_slots() ?? []).Select(slot => new FilterOption(slot.Index, slot.Name)).ToList();
        if (options.All(o => o.Index != _index))
        {
            options.Insert(0, new FilterOption(_index, string.Empty, IsMissing: true));
        }

        return options;
    }
}
