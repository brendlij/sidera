using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Views.Framing;

public partial class FramingView : UserControl
{
    // While the page is shown, where the telescope points is read four times a second, so that the red field follows a slew; it is only a read of the mount and the last solve.
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public FramingView()
    {
        InitializeComponent();
        _live.Tick += (_, _) => (DataContext as FramingViewModel)?.RefreshCurrent();
        AttachedToVisualTree += (_, _) => _live.Start();
        DetachedFromVisualTree += (_, _) => _live.Stop();
    }
}
