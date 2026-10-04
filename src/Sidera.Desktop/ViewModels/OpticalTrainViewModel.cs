using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The optical train of the rig of a camera, as a small form: what is configured (focal length, aperture, pixel size, sensor pixels) and, beside
/// it and clearly apart, what is derived (pixel scale, sensor size, field of view) and what the camera reports. A pixel size or a sensor size
/// left empty is taken from the camera when it reports one; a value that is entered wins. The derived values are computed by
/// <see cref="OpticalTrainGeometry"/> as the fields change and are never stored; only the entered values are.
/// </summary>
public sealed partial class OpticalTrainViewModel : ViewModelBase, IDisposable
{
    private readonly CameraViewModel _camera;
    private readonly EquipmentService? _service;
    private bool _loading;

    public OpticalTrainViewModel(CameraViewModel camera, OpticalTrain? configured, EquipmentService? service)
    {
        _camera = camera;
        _service = service;
        _loading = true;
        FocalLengthText = Show(configured?.FocalLengthMm);
        ApertureText = Show(configured?.ApertureMm);
        PixelSizeXText = Show(configured?.PixelSizeXMicrons);
        PixelSizeYText = Show(configured?.PixelSizeYMicrons);
        SensorWidthText = configured?.SensorWidthPixels?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SensorHeightText = configured?.SensorHeightPixels?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        HasConfiguration = configured is not null;
        _loading = false;
        _camera.Refreshed += OnCameraRefreshed;
        Recompute();
    }

    /// <summary>The optics can be saved: there is an equipment service to keep them.</summary>
    public bool CanEdit => _service is not null;

    public bool HasConfiguration { get; private set; }

    // ---- Configured (editable)

    [ObservableProperty]
    public partial string FocalLengthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApertureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PixelSizeXText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PixelSizeYText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SensorWidthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SensorHeightText { get; set; } = string.Empty;

    // ---- Device reported and derived (read-only)

    /// <summary>What the camera says about its sensor; asks the user to connect when it has not been read yet.</summary>
    [ObservableProperty]
    public partial string ReportedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PixelScaleText { get; private set; } = RigViewModel.PixelScaleOf(OpticalTrainGeometry.Compute(null, null, null, null, null));

    [ObservableProperty]
    public partial string FieldOfViewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SensorSizeText { get; private set; } = string.Empty;

    /// <summary>Where the pixel size and the sensor size in the calculation come from.</summary>
    [ObservableProperty]
    public partial string SourceText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; private set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    partial void OnFocalLengthTextChanged(string value) => OnEdited();
    partial void OnApertureTextChanged(string value) => OnEdited();
    partial void OnPixelSizeXTextChanged(string value) => OnEdited();
    partial void OnPixelSizeYTextChanged(string value) => OnEdited();
    partial void OnSensorWidthTextChanged(string value) => OnEdited();
    partial void OnSensorHeightTextChanged(string value) => OnEdited();

    private void OnEdited()
    {
        if (!_loading)
        {
            Recompute();
        }
    }

    private void OnCameraRefreshed(object? sender, EventArgs e) => Recompute();

    private SensorGeometry? Reported => SensorGeometry.From((_camera.DeviceModel as ICameraControl)?.Capabilities.Value);

    // The form as an optical train; false with the reason when a field is not a number that is allowed.
    private bool TryBuild(out OpticalTrain? train, out string? problem)
    {
        train = null;
        problem = null;
        if (!TryPositive(FocalLengthText, "focal length", out var focal, out problem)
            || !TryPositive(ApertureText, "aperture", out var aperture, out problem)
            || !TryPositive(PixelSizeXText, "pixel size (width)", out var px, out problem)
            || !TryPositive(PixelSizeYText, "pixel size (height)", out var py, out problem)
            || !TryCount(SensorWidthText, "sensor width", out var width, out problem)
            || !TryCount(SensorHeightText, "sensor height", out var height, out problem))
        {
            return false;
        }

        if (focal is null)
        {
            problem = "The focal length is required.";
            return false;
        }

        train = new OpticalTrain(focal.Value, aperture, px, py, width, height);
        return true;
    }

    private void Recompute()
    {
        var reported = Reported;
        ReportedText = reported is null
            ? "Connect the camera to read its pixel size and sensor size."
            : $"Camera reports: {ReportedPixelSize(reported)}, {ReportedSensor(reported)}.";

        TryBuild(out var train, out _);
        // A form that is not valid (yet) still shows what the camera alone implies, never a stale answer.
        var geometry = OpticalTrainGeometry.Resolve(train, reported);
        PixelScaleText = RigViewModel.PixelScaleOf(geometry);
        FieldOfViewText = RigViewModel.FieldOfViewOf(geometry);
        SensorSizeText = geometry is { SensorWidthMm: { } w, SensorHeightMm: { } h }
            ? string.Create(CultureInfo.InvariantCulture, $"{w:0.##} × {h:0.##} mm")
            : "Not set";
        SourceText = string.Create(
            CultureInfo.InvariantCulture,
            $"Pixel size: {Source(geometry.PixelSizeSource)}. Sensor pixels: {Source(geometry.SensorPixelsSource)}.");
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Saves what is entered; the first field that is not valid is named and nothing is saved.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Save()
    {
        ProblemText = string.Empty;
        if (!TryBuild(out var train, out var problem))
        {
            ProblemText = problem ?? "The optics are not valid.";
            return;
        }

        var result = _service!.SetCameraOptics(_camera.DeviceIdText, train);
        if (!result.Succeeded)
        {
            ProblemText = result.Problem ?? "The optics could not be saved.";
        }
    }

    /// <summary>Forgets the optics of the rig.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Remove()
    {
        ProblemText = string.Empty;
        var result = _service!.SetCameraOptics(_camera.DeviceIdText, null);
        if (!result.Succeeded)
        {
            ProblemText = result.Problem ?? "The optics could not be removed.";
        }
    }

    private static bool TryPositive(string text, string what, out double? value, out string? problem)
    {
        value = null;
        problem = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var normalized = trimmed.Contains('.') ? trimmed : trimmed.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number <= 0)
        {
            problem = $"The {what} must be a number greater than zero.";
            return false;
        }

        value = number;
        return true;
    }

    private static bool TryCount(string text, string what, out int? value, out string? problem)
    {
        value = null;
        problem = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            problem = $"The {what} must be a whole number of pixels greater than zero.";
            return false;
        }

        value = number;
        return true;
    }

    private static string Show(double? value) => value is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;

    private static string ReportedPixelSize(SensorGeometry s) => (s.PixelSizeXMicrons, s.PixelSizeYMicrons) switch
    {
        ({ } x, { } y) when Math.Abs(x - y) < 1e-9 => string.Create(CultureInfo.InvariantCulture, $"{x:0.##} µm pixels"),
        ({ } x, { } y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.##} × {y:0.##} µm pixels"),
        _ => "no pixel size",
    };

    private static string ReportedSensor(SensorGeometry s) => s is { WidthPixels: { } w, HeightPixels: { } h }
        ? string.Create(CultureInfo.InvariantCulture, $"{w} × {h} px")
        : "no sensor size";

    private static string Source(GeometrySource source) => source switch
    {
        GeometrySource.Configured => "configured",
        GeometrySource.DeviceReported => "reported by the camera",
        _ => "unknown",
    };

    public void Dispose() => _camera.Refreshed -= OnCameraRefreshed;
}
