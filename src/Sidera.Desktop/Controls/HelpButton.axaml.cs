using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Controls;

/// <summary>
/// The "?" in the header of a page. <see cref="Context"/> says where it is ("session", "settings:platesolving"); pressing it opens Help at the article of that context. It needs no view model of its own:
/// the window has one that knows Help.
/// </summary>
public partial class HelpButton : UserControl
{
    public static readonly StyledProperty<string?> ContextProperty = AvaloniaProperty.Register<HelpButton, string?>(nameof(Context));

    public HelpButton() => InitializeComponent();

    public string? Context { get => GetValue(ContextProperty); set => SetValue(ContextProperty, value); }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.OpenHelp(Context);
        }
    }
}
