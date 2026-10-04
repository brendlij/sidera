using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Astra.Desktop.Controls;

/// <summary>
/// A button that acts while it is held: <see cref="PressCommand"/> when the pointer goes down, <see cref="ReleaseCommand"/> once
/// when it comes up or the pointer is lost for any reason (the window loses focus, the control is removed). For moving something
/// only as long as the user pushes; it is never a toggle.
/// </summary>
public sealed class HoldButton : Button
{
    public static readonly StyledProperty<ICommand?> PressCommandProperty =
        AvaloniaProperty.Register<HoldButton, ICommand?>(nameof(PressCommand));

    public static readonly StyledProperty<ICommand?> ReleaseCommandProperty =
        AvaloniaProperty.Register<HoldButton, ICommand?>(nameof(ReleaseCommand));

    private bool _held;

    public HoldButton()
    {
        LostFocus += (_, _) => Release();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public ICommand? PressCommand
    {
        get => GetValue(PressCommandProperty);
        set => SetValue(PressCommandProperty, value);
    }

    public ICommand? ReleaseCommand
    {
        get => GetValue(ReleaseCommandProperty);
        set => SetValue(ReleaseCommandProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_held || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _held = true;
        e.Pointer.Capture(this);
        Execute(PressCommand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Release();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Release();
    }

    private Window? _window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Deactivated += OnWindowDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }

        Release();
    }

    // The window loses the foreground (alt-tab, a dialog): the pointer events may never come, the movement must end.
    private void OnWindowDeactivated(object? sender, EventArgs e) => Release();

    private void Release()
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        Execute(ReleaseCommand);
    }

    private void Execute(ICommand? command)
    {
        if (command?.CanExecute(CommandParameter) == true)
        {
            command.Execute(CommandParameter);
        }
    }
}
