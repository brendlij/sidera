using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Astra.Desktop.Controls;

/// <summary>
/// One setting on one line: the name on the left, the input on the right and, when the value has one, its unit
/// after it. Every number field of every editor is one of these, so they line up and look alike.
/// </summary>
public sealed class FieldRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FieldRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<FieldRow, string?>(nameof(Unit));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }
}

/// <summary>A fact about something: a name on the left, its value on the right. Read-only; for the information of a device or a rig.</summary>
public sealed class InfoRow : Avalonia.Controls.Primitives.TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<InfoRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<InfoRow, string?>(nameof(Value));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}

/// <summary>A setting whose input is wide (a picker): the name above, the input below it.</summary>
public sealed class LabeledField : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<LabeledField, string?>(nameof(Label));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}

/// <summary>What a place says when it has nothing to show yet: a title, a sentence, and perhaps an action as content.</summary>
public sealed class EmptyState : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Message));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>The top of a page: its title and a line under it on the left, and what can be done on the page as content on the right.</summary>
public sealed class PageHeader : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Subtitle));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
}

/// <summary>
/// One section of a device workspace: a quiet title and its content, without a box around it. Sections are laid out in a wrapping
/// panel, so that they fill the width of the window in as many columns as fit; a section that needs more room sets its own width.
/// </summary>
public sealed class WorkspaceSection : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<WorkspaceSection, string?>(nameof(Header));

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
}
