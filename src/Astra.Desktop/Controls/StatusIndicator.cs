using Astra.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Astra.Desktop.Controls;

/// <summary>A small dot and a word: how Astra shows the state of a device, a rig or the sequence. Not a capsule.</summary>
public sealed class StatusIndicator : TemplatedControl
{
    public static readonly StyledProperty<StatusKind> KindProperty =
        AvaloniaProperty.Register<StatusIndicator, StatusKind>(nameof(Kind));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusIndicator, string?>(nameof(Text));

    public StatusIndicator() => UpdateKind(Kind);

    public StatusKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            UpdateKind(change.GetNewValue<StatusKind>());
        }
    }

    private void UpdateKind(StatusKind kind)
    {
        PseudoClasses.Set(":neutral", kind == StatusKind.Neutral);
        PseudoClasses.Set(":ok", kind == StatusKind.Ok);
        PseudoClasses.Set(":active", kind == StatusKind.Active);
        PseudoClasses.Set(":warning", kind == StatusKind.Warning);
        PseudoClasses.Set(":error", kind == StatusKind.Error);
    }
}
