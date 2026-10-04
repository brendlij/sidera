using Avalonia;
using Avalonia.Controls;

namespace Astra.Desktop.Views.Equipment;

/// <summary>
/// The frame of a device detail: the header with the connection, and the four tabs. The view of each kind of device
/// brings the content of its tabs: what the device is and does now (Overview), what can be done with it by hand
/// (Controls) and what can be set on it (Settings, none yet). The Driver Info is the same for every device.
/// </summary>
public partial class DeviceDetailShell : UserControl
{
    public static readonly StyledProperty<object?> OverviewContentProperty =
        AvaloniaProperty.Register<DeviceDetailShell, object?>(nameof(OverviewContent));

    public static readonly StyledProperty<object?> ControlsContentProperty =
        AvaloniaProperty.Register<DeviceDetailShell, object?>(nameof(ControlsContent));

    public static readonly StyledProperty<object?> SettingsContentProperty =
        AvaloniaProperty.Register<DeviceDetailShell, object?>(nameof(SettingsContent));

    public DeviceDetailShell()
    {
        InitializeComponent();
    }

    /// <summary>What the device is and what it is doing.</summary>
    public object? OverviewContent
    {
        get => GetValue(OverviewContentProperty);
        set => SetValue(OverviewContentProperty, value);
    }

    /// <summary>What can be done with the device by hand.</summary>
    public object? ControlsContent
    {
        get => GetValue(ControlsContentProperty);
        set => SetValue(ControlsContentProperty, value);
    }

    /// <summary>The settings of the device; <c>null</c> while the kind of device has none that Astra implements.</summary>
    public object? SettingsContent
    {
        get => GetValue(SettingsContentProperty);
        set => SetValue(SettingsContentProperty, value);
    }
}
