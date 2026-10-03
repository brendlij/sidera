using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Rigs;
using Astra.Runtime;

namespace Astra.Desktop.ViewModels;

/// <summary>A logical imaging rig, read-only: which devices form it and the optical train it describes.</summary>
public sealed class RigViewModel : ViewModelBase
{
    private readonly Rig _rig;

    public RigViewModel(Rig rig, AstraRuntimeHost host, IReadOnlyList<CameraViewModel> cameras)
    {
        _rig = rig;
        Camera = cameras.FirstOrDefault(c => c.CameraId == rig.CameraId);
        CameraText = Describe(host, rig.CameraId);
        FocuserText = rig.FocuserId is { } focuser ? Describe(host, focuser) : "None";
        FilterWheelText = rig.FilterWheelId is { } wheel ? Describe(host, wheel) : "None";
    }

    public string Name => _rig.Name;
    public string RigIdText => _rig.Id.Value;

    /// <summary>The rig's camera card, for its connection state; <c>null</c> if it has none.</summary>
    public CameraViewModel? Camera { get; }

    public string CameraText { get; }
    public string FocuserText { get; }
    public string FilterWheelText { get; }

    public string FocalLengthText => Format($"{_rig.Optics.FocalLengthMm:0.##} mm");
    public string ApertureText => Format($"{_rig.Optics.ApertureMm:0.##} mm");
    public string PixelSizeText => Format($"{_rig.Optics.PixelSizeMicrons:0.##} µm");
    public string SensorText => Format($"{_rig.Optics.SensorWidthMm:0.##} × {_rig.Optics.SensorHeightMm:0.##} mm");
    public string ResolutionText => Format($"{_rig.Optics.ResolutionWidth} × {_rig.Optics.ResolutionHeight} px");

    private static string Describe(AstraRuntimeHost host, DeviceId id) =>
        host.DeviceRegistry.TryGet(id, out var device) && device is not null
            ? $"{device.Name} · {id.Value}"
            : id.Value;

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
