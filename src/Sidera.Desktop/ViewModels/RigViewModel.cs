using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>One part of a rig: its role, the device that plays it (if the rig has one) and what it says right now.</summary>
public sealed class RigMemberViewModel(string role, DeviceViewModelBase? device)
{
    /// <summary>"Camera", "Focuser" or "Filter Wheel".</summary>
    public string Role { get; } = role;

    /// <summary>The device card of the member; <c>null</c> when the rig has no such device.</summary>
    public DeviceViewModelBase? Device { get; } = device;

    public bool IsConfigured => Device is not null;

    /// <summary>Opens the page of this device in the workspace of the rig. Set by the page that shows the rigs.</summary>
    public System.Windows.Input.ICommand? OpenCommand { get; set; }

    public string NameText => Device?.Name ?? "Not configured";
}

/// <summary>
/// A logical imaging rig, read-only: which devices form it, the optical train it describes, and what its parts are
/// doing. A rig is a grouping of equipment, never a replacement for it: its devices stay available on their own.
/// The state is read from the device view models, so a rig can never disagree with the devices it names.
/// </summary>
public sealed partial class RigViewModel : ViewModelBase
{
    private readonly Rig _rig;

    public RigViewModel(
        Rig rig,
        SideraRuntimeHost host,
        IReadOnlyList<CameraViewModel> cameras,
        IReadOnlyList<FocuserViewModel>? focusers = null,
        IReadOnlyList<FilterWheelViewModel>? filterWheels = null)
    {
        _rig = rig;
        Camera = cameras.FirstOrDefault(c => c.CameraId == rig.CameraId);
        Focuser = rig.FocuserId is { } focuserId ? focusers?.FirstOrDefault(f => f.DeviceIdText == focuserId.Value) : null;
        FilterWheel = rig.FilterWheelId is { } wheelId ? filterWheels?.FirstOrDefault(w => w.DeviceIdText == wheelId.Value) : null;
        CameraText = Describe(host, rig.CameraId);
        FocuserText = rig.FocuserId is { } focuser ? Describe(host, focuser) : NotConfigured;
        FilterWheelText = rig.FilterWheelId is { } wheel ? Describe(host, wheel) : NotConfigured;

        Members = [new RigMemberViewModel("Camera", Camera), new RigMemberViewModel("Focuser", Focuser), new RigMemberViewModel("Filter Wheel", FilterWheel)];
        foreach (var device in Members.Select(m => m.Device).OfType<DeviceViewModelBase>())
        {
            device.Refreshed += (_, _) => RefreshState();
        }

        RefreshState();
    }

    /// <summary>What a rig shows for a device it has none of.</summary>
    public const string NotConfigured = "Not configured";

    /// <summary>What a rig shows for a device of it that is not connected.</summary>
    public const string NotConnected = "Not connected";

    public string Name => _rig.Name;
    public string RigIdText => _rig.Id.Value;

    /// <summary>The rig's camera card, for its connection state; <c>null</c> if it has none.</summary>
    public CameraViewModel? Camera { get; }

    /// <summary>The rig's focuser, if it has one and the equipment lists it.</summary>
    public FocuserViewModel? Focuser { get; }

    /// <summary>The rig's filter wheel, if it has one and the equipment lists it.</summary>
    public FilterWheelViewModel? FilterWheel { get; }

    /// <summary>The camera, the focuser and the filter wheel, in this order, with the ones the rig does not have marked as such.</summary>
    public IReadOnlyList<RigMemberViewModel> Members { get; }

    public string CameraText { get; }
    public string FocuserText { get; }
    public string FilterWheelText { get; }

    public bool HasFocuser => _rig.FocuserId is not null;
    public bool HasFilterWheel => _rig.FilterWheelId is not null;

    /// <summary>The rig's id of the camera, for matching it with running sequences.</summary>
    public RigId Id => _rig.Id;

    /// <summary>The rotator of the rig; <c>null</c> for a rig without one.</summary>
    public DeviceId? RotatorId => _rig.RotatorId;

    /// <summary>The calibration of the rotator: how its position relates to the rotation of the sky in the image; <c>null</c> until calibrated.</summary>
    public Sidera.Core.Rotators.RotatorSkyModel? RotatorModel => _rig.RotatorModel;

    /// <summary>Selects the rig on the equipment page, which shows its detail. Set by the page that lists the rigs.</summary>
    public ICommand? OpenCommand { get; set; }

    /// <summary>This is the rig whose detail the equipment page shows; the browser marks its row.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The optics as configured; <c>null</c> for a rig that has none.</summary>
    public OpticalTrain? Optics => _rig.Optics;

    /// <summary>The geometry of the rig: configured values first, then what the camera reports, then the camera database. Derived every time, never stored.</summary>
    public OpticalTrainGeometry Geometry =>
        OpticalTrainGeometry.Resolve(_rig.Optics, SensorGeometry.For(Camera?.DeviceModel));

    private const string Unknown = "Not set";

    /// <summary>The optical train on one line, for example "750 mm · f/5 · 3.76 µm pixels".</summary>
    public string OpticsText
    {
        get
        {
            if (_rig.Optics is not { } o)
            {
                return Unknown;
            }

            var text = Format($"{o.FocalLengthMm:0.##} mm") + FRatioText(o);
            return Geometry.PixelSizeXMicrons is { } px ? text + Format($" · {px:0.##} µm pixels") : text;
        }
    }

    /// <summary>The short form for a card: "750 mm · f/5".</summary>
    public string OpticsShortText => _rig.Optics is not { } o ? Unknown : Format($"{o.FocalLengthMm:0.##} mm") + FRatioText(o);

    private static string FRatioText(OpticalTrain optics) => optics.FocalRatio is { } ratio ? Format($" · f/{ratio:0.#}") : string.Empty;

    public string FocalLengthText => _rig.Optics is { } o ? Format($"{o.FocalLengthMm:0.##} mm") : Unknown;
    public string ApertureText => _rig.Optics?.ApertureMm is { } a ? Format($"{a:0.##} mm") : Unknown;
    public string PixelSizeText => PixelSizeOf(Geometry);
    public string SensorText => Geometry is { SensorWidthMm: { } w, SensorHeightMm: { } h } ? Format($"{w:0.##} × {h:0.##} mm") : Unknown;
    public string ResolutionText => Geometry is { SensorWidthPixels: { } w, SensorHeightPixels: { } h } ? Format($"{w} × {h} px") : Unknown;

    /// <summary>The pixel scale, "1.03 \"/px" (or "1.03 × 1.04 \"/px" when the pixels are not square); "Not set" while a source is missing.</summary>
    public string PixelScaleText => PixelScaleOf(Geometry);

    /// <summary>The field of view, "1.79° × 1.20°"; "Not set" while a source is missing.</summary>
    public string FieldOfViewText => FieldOfViewOf(Geometry);

    internal static string PixelSizeOf(OpticalTrainGeometry g) => (g.PixelSizeXMicrons, g.PixelSizeYMicrons) switch
    {
        ({ } x, { } y) when Math.Abs(x - y) < 1e-9 => Format($"{x:0.##} µm"),
        ({ } x, { } y) => Format($"{x:0.##} × {y:0.##} µm"),
        ({ } x, null) => Format($"{x:0.##} µm"),
        (null, { } y) => Format($"{y:0.##} µm"),
        _ => Unknown,
    };

    internal static string PixelScaleOf(OpticalTrainGeometry g) => (g.PixelScaleXArcsecPerPixel, g.PixelScaleYArcsecPerPixel) switch
    {
        ({ } x, { } y) when Math.Abs(x - y) < 0.005 => Format($"{x:0.00} \"/px"),
        ({ } x, { } y) => Format($"{x:0.00} × {y:0.00} \"/px"),
        ({ } x, null) => Format($"{x:0.00} \"/px"),
        (null, { } y) => Format($"{y:0.00} \"/px"),
        _ => Unknown,
    };

    internal static string FieldOfViewOf(OpticalTrainGeometry g) =>
        g is { FieldOfViewXDegrees: { } x, FieldOfViewYDegrees: { } y } ? Format($"{x:0.00}° × {y:0.00}°") : Unknown;

    /// <summary>All the rig's devices connected, some of them, or none.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusKind StatusKind { get; private set; }

    /// <summary>What the camera of the rig is doing ("Idle", "Exposing 2.1 / 5 s"), or "Not connected".</summary>
    [ObservableProperty]
    public partial string ActivityText { get; private set; } = string.Empty;

    /// <summary>The focuser position, or "Not connected"; meaningful when the rig has a focuser.</summary>
    [ObservableProperty]
    public partial string FocusText { get; private set; } = string.Empty;

    /// <summary>The filter in the light path, or "Not connected"; meaningful when the rig has a filter wheel.</summary>
    [ObservableProperty]
    public partial string FilterText { get; private set; } = string.Empty;

    private void RefreshState()
    {
        var present = Members.Select(m => m.Device).OfType<DeviceViewModelBase>().ToList();
        var connected = present.Count(d => d.IsConnected);
        (StatusText, StatusKind) = present.Count == 0 ? ("No devices", StatusKind.Neutral)
            : connected == present.Count ? ("Connected", StatusKind.Ok)
            : connected == 0 ? ("Disconnected", StatusKind.Neutral)
            : ("Partly connected", StatusKind.Warning);

        ActivityText = Camera is { IsConnected: true } ? Camera.ActivityText : NotConnected;
        FocusText = Focuser is { IsConnected: true } ? Focuser.PositionText : NotConnected;
        FilterText = FilterWheel is { IsConnected: true } ? FilterWheel.CurrentFilterText : NotConnected;
    }

    private static string Describe(SideraRuntimeHost host, DeviceId id) =>
        host.DeviceRegistry.TryGet(id, out var device) && device is not null
            ? $"{device.Name} · {id.Value}"
            : id.Value;

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
