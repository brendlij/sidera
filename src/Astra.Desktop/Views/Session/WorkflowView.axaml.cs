using System;
using System.Linq;
using Astra.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Astra.Desktop.Views.Session;

/// <summary>
/// The workflow of the session. Reordering by drag and drop is split in two: this view detects the drag (it starts on the
/// handle of a row, never on the row, so a click still selects and the inspector can never start one) and finds the row
/// and the half of it under the pointer; the draft judges the place, draws it through the rows, and moves the step.
/// </summary>
public partial class WorkflowView : UserControl
{
    private static readonly DataFormat<string> StepFormat = DataFormat.CreateStringApplicationFormat("astra.sequence-step");

    // The page scrolls when a drag is within this distance of its top or bottom edge, by this much per update.
    private const double ScrollEdge = 56;
    private const double ScrollStep = 18;

    public WorkflowView()
    {
        InitializeComponent();
        StepList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        StepList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        StepList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        StepList.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        StepList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private SequenceDraftViewModel? Draft => (DataContext as SessionPageViewModel)?.Draft;

    // Alt+Up and Alt+Down move the selected step like Move Up and Move Down do.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down) || Draft is not { } draft)
        {
            return;
        }

        var command = e.Key == Key.Up ? draft.MoveStepUpCommand : draft.MoveStepDownCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            FocusSelected(draft);
        }

        e.Handled = true;
    }

    // The rows are rebuilt after every move; the keyboard stays with the step that was moved.
    private void FocusSelected(SequenceDraftViewModel draft) =>
        Dispatcher.UIThread.Post(
            () => (draft.SelectedStep is { } step ? StepList.ContainerFromItem(step) : null)?.Focus(), DispatcherPriority.Loaded);

    // The drag starts when the handle of a row is pressed with the left button, and lasts until it is dropped or cancelled.
    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Draft is not { } draft
            || !e.GetCurrentPoint(StepList).Properties.IsLeftButtonPressed
            || HandleOf(e.Source) is not { DataContext: StepDraftViewModel step }
            || !draft.BeginDrag(step.Id))
        {
            return;
        }

        e.Handled = true;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(StepFormat, step.Id.ToString("N")));
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            draft.EndDrag();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (Draft is not { } draft || SourceOf(e) is not { } source)
        {
            return;
        }

        ScrollNearEdge(e);
        var plan = PlanAt(draft, source, e);
        draft.ShowDrop(plan);
        e.DragEffects = plan is { IsMove: true } ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => Draft?.ShowDrop(null);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Draft is not { } draft || SourceOf(e) is not { } source)
        {
            return;
        }

        var plan = PlanAt(draft, source, e);
        var moved = plan?.Over is { } over && draft.Drop(source, over.Id, plan.MarkerBefore ? DropPlacement.Before : DropPlacement.After);
        draft.EndDrag();
        if (moved)
        {
            FocusSelected(draft);
        }

        e.DragEffects = moved ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static Guid? SourceOf(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(StepFormat) is { } text && Guid.TryParse(text, out var id) ? id : null;

    private static Control? HandleOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("drag-handle"));

    // The row the pointer is nearest to (the gap between two rows counts for the row above it, and below the last row
    // the last one), and whether it is over the upper or the lower half of it.
    private StepDropPlan? PlanAt(SequenceDraftViewModel draft, Guid source, DragEventArgs e)
    {
        var y = e.GetPosition(StepList).Y;
        for (var i = 0; i < StepList.ItemCount; i++)
        {
            if (StepList.ContainerFromIndex(i) is not { } container || container.TranslatePoint(default, StepList) is not { } top)
            {
                continue;
            }

            if (y <= top.Y + container.Bounds.Height || i == StepList.ItemCount - 1)
            {
                var placement = y < top.Y + container.Bounds.Height / 2 ? DropPlacement.Before : DropPlacement.After;
                return StepList.Items[i] is StepDraftViewModel row ? draft.PlanDrop(source, row.Id, placement) : null;
            }
        }

        return null;
    }

    private void ScrollNearEdge(DragEventArgs e)
    {
        if (this.FindAncestorOfType<ScrollViewer>() is not { } scroll)
        {
            return;
        }

        var y = e.GetPosition(scroll).Y;
        var delta = y < ScrollEdge ? -ScrollStep : y > scroll.Bounds.Height - ScrollEdge ? ScrollStep : 0;
        if (delta != 0)
        {
            var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, max));
        }
    }
}
