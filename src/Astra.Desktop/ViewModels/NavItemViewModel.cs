using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>One entry of the sidebar: a page, its name, its icon (the key of a geometry in the theme) and whether it is the current page.</summary>
public sealed partial class NavItemViewModel(AppPage page, string title, string iconKey, ICommand command) : ObservableObject
{
    public AppPage Page { get; } = page;
    public string Title { get; } = title;

    /// <summary>The resource key of the icon, for example "IconSession".</summary>
    public string IconKey { get; } = iconKey;

    /// <summary>Goes to the page.</summary>
    public ICommand Command { get; } = command;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
