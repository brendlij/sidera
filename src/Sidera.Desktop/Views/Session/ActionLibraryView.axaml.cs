using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Views.Session;

public partial class ActionLibraryView : UserControl
{
    private ActionLibraryViewModel? _library;

    public ActionLibraryView()
    {
        InitializeComponent();

        // The search box has the focus when the library opens, and Down goes from it into the list: the library is used with the keyboard.
        DataContextChanged += (_, _) =>
        {
            if (_library is not null)
            {
                _library.PropertyChanged -= OnLibraryChanged;
            }

            _library = DataContext as ActionLibraryViewModel;
            if (_library is not null)
            {
                _library.PropertyChanged += OnLibraryChanged;
            }
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down)
            {
                Items.Focus();
                e.Handled = true;
            }
        };
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionLibraryViewModel.IsOpen) && _library is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
        }
    }
}
