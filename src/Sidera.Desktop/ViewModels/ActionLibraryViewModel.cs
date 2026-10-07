using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.ViewModels;

/// <summary>An action of the library: what it is called, what it does, and where it is found.</summary>
public sealed record ActionItem(SessionActionKind Kind, string Title, string Description, ActionCategory Category)
{
    public string CategoryText => Category switch
    {
        ActionCategory.FilterWheel => "Filter wheel",
        _ => Category.ToString(),
    };
}

/// <summary>A group of the library, as a button that narrows the list; <see cref="Category"/> is <c>null</c> for "All".</summary>
public sealed partial class CategoryChip(ActionCategory? category, string name, Action<CategoryChip> choose) : ObservableObject
{
    public ActionCategory? Category { get; } = category;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    [RelayCommand]
    private void Select() => choose(this);
}

/// <summary>
/// The library of actions: a panel that opens where an action is to be added, shows only the actions that can go there, narrows by a group or by what is typed, and adds with a click or with Enter. It
/// is how an action gets into a block, a preparation, the start or the end; nothing depends on dragging.
/// </summary>
public sealed partial class ActionLibraryViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;
    private ActionOwner _target = ActionOwner.Start;

    internal ActionLibraryViewModel(SessionEditorViewModel owner)
    {
        _owner = owner;
        Categories = [new CategoryChip(null, "All", Choose), .. Enum.GetValues<ActionCategory>().Select(c => new CategoryChip(c, c == ActionCategory.FilterWheel ? "Filter wheel" : c.ToString(), Choose))];
        SelectedCategory = Categories[0];
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>"Add an action to the start of the session", "… to the preparation of M42", "… to a block".</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    public IReadOnlyList<CategoryChip> Categories { get; }

    [ObservableProperty]
    public partial CategoryChip SelectedCategory { get; set; }

    public ObservableCollection<ActionItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    [ObservableProperty]
    public partial ActionItem? Selected { get; set; }

    partial void OnSearchChanged(string value) => Fill();

    partial void OnSelectedCategoryChanged(CategoryChip value)
    {
        foreach (var chip in Categories)
        {
            chip.IsSelected = ReferenceEquals(chip, value);
        }

        Fill();
    }

    private void Choose(CategoryChip chip) => SelectedCategory = chip;

    /// <summary>Opens the library for a list of actions.</summary>
    public void Open(ActionOwner place)
    {
        _target = place;
        Heading = place.Place switch
        {
            ActionPlace.Start => "Add an action to the start of the session",
            ActionPlace.End => "Add an action to the end of the session",
            ActionPlace.Preparation => "Add an action to the preparation of the target",
            _ => "Add an action to the block",
        };
        Search = string.Empty;
        SelectedCategory = Categories[0];
        Fill();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    /// <summary>Adds the action to the list the library was opened for, and closes it.</summary>
    [RelayCommand]
    public void Choose(ActionItem? item)
    {
        if (item is null)
        {
            return;
        }

        IsOpen = false;
        _owner.AddAction(_target, item.Kind);
    }

    /// <summary>Adds the action that is selected (Enter in the search box).</summary>
    [RelayCommand]
    public void ChooseSelected() => Choose(Selected ?? Items.FirstOrDefault());

    private void Fill()
    {
        var search = Search.Trim();
        var items = ActionCatalog.For(_target.Scope)
            .Where(a => SelectedCategory.Category is null || a.Category == SelectedCategory.Category)
            .Where(a => search.Length == 0 || a.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || a.Category.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => search.Length > 0 && a.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ? 0 : 1) // what is called like the search comes first, what only talks about it after
            .Select(a => new ActionItem(a.Kind, a.Title, a.Description, a.Category))
            .ToList();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasItems));
    }
}
