using Avalonia.Controls;
using Avalonia.Input;
using Sidera.Desktop.Notifications;

namespace Sidera.Desktop.Views.Shell;

public partial class NotificationHostView : UserControl
{
    public NotificationHostView()
    {
        InitializeComponent();
    }

    private static NotificationViewModel? Of(object? sender) => (sender as Control)?.DataContext as NotificationViewModel;

    private void OnPointerEntered(object? sender, PointerEventArgs e) => Of(sender)?.SetHovered(true);

    private void OnPointerExited(object? sender, PointerEventArgs e) => Of(sender)?.SetHovered(false);

    // Escape closes the message that has the focus. A message never takes the focus by itself.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Of(sender) is { } notification)
        {
            notification.DismissCommand.Execute(null);
            e.Handled = true;
        }
    }
}
