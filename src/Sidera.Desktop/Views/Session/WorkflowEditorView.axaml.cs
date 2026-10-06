using Avalonia.Controls;
using Avalonia.Threading;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Views.Session;

public partial class WorkflowEditorView : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = System.TimeSpan.FromSeconds(1) };

    public WorkflowEditorView()
    {
        InitializeComponent();

        // The countdown to the meridian follows the clock while the editor is on screen; nothing else needs it.
        _clock.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            Refresh();
            _clock.Start();
        };
        DetachedFromVisualTree += (_, _) => _clock.Stop();
    }

    private void Refresh()
    {
        if (DataContext is WorkflowEditorViewModel editor)
        {
            editor.RefreshMeridian();
            editor.RefreshConditions();
        }
    }
}
