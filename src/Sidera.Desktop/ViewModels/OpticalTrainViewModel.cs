using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// <summary>A rotator that can be chosen for a rig: its device id, and its name.</summary>
public sealed record RotatorChoice(string? Id, string Text);

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
        RotatorChoices = [new RotatorChoice(null, "None"), .. (service?.Configuration.Devices ?? []).Where(d => d.Type == DeviceType.Rotator).Select(d => new RotatorChoice(d.Id, d.Name))];
        var currentRotator = service?.Configuration.Rigs.FirstOrDefault(r => string.Equals(r.CameraId, camera.DeviceIdText, StringComparison.OrdinalIgnoreCase))?.RotatorId;
        _selectedRotator = RotatorChoices.FirstOrDefault(c => string.Equals(c.Id, currentRotator, StringComparison.OrdinalIgnoreCase)) ?? RotatorChoices[0];
        _loading = false;
        _camera.Refreshed += OnCameraRefreshed;
        Recompute();
    }

    // ---- The rotator of the rig

    /// <summary>The rotators that can be chosen, and "None": a rig without a rotator is complete as it is.</summary>
    public IReadOnlyList<RotatorChoice> RotatorChoices { get; }

    public bool HasRotatorChoices => RotatorChoices.Count > 1;

    private RotatorChoice _selectedRotator;

    /// <summary>The rotator of the rig of this camera. Choosing one only records the association; nothing is connected and nothing moves.</summary>
    public RotatorChoice SelectedRotator
    {
        get => _selectedRotator;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedRotator) || !SetProperty(ref _selectedRotator, value) || _loading || _service is null)
            {
                return;
            }

            var result = _service.SetCameraRotator(_camera.DeviceIdText, value.Id);
            ProblemText = result.Succeeded ? string.Empty : result.Problem ?? "The rotator could not be set.";
        }
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
            OnPropertyChanged(nameof(HasOverride));
        }
    }

    private void OnCameraRefreshed(object? sender, EventArgs e) => Recompute();

    // What the camera itself says (the "Camera reports" line), and what is known about the sensor altogether: that, completed from the camera database for a known camera.
    private SensorGeometry? Reported => SensorGeometry.From((_camera.DeviceModel as ICameraControl)?.Capabilities.Value);

    private SensorGeometry? Known => SensorGeometry.For(_camera.DeviceModel);

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
        var geometry = OpticalTrainGeometry.Resolve(train, Known);
        PixelScaleText = RigViewModel.PixelScaleOf(geometry);
        FieldOfViewText = RigViewModel.FieldOfViewOf(geometry);
        SensorSizeText = geometry is { SensorWidthMm: { } w, SensorHeightMm: { } h }
            ? string.Create(CultureInfo.InvariantCulture, $"{w:0.##} × {h:0.##} mm")
            : "Not set";
        SourceText = string.Create(
            CultureInfo.InvariantCulture,
            $"Pixel size: {Source(geometry.PixelSizeSource)}. Sensor pixels: {Source(geometry.SensorPixelsSource)}.");
        ShowResolved(geometry);
        SaveCommand.NotifyCanExecuteChanged();
        RevertToAutomaticCommand.NotifyCanExecuteChanged();
    }

    // ---- The resolved sensor geometry, each value with its source

    /// <summary>The camera as the camera database knows it ("ZWO ASI2600MC Pro"); empty for a camera that is not in it.</summary>
    [ObservableProperty]
    public partial string CameraIdentityText { get; private set; } = string.Empty;

    /// <summary>The sensor ("Sony IMX571") as the driver or the database names it; empty when nobody does.</summary>
    [ObservableProperty]
    public partial string SensorNameText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResolutionText { get; private set; } = NotSet;

    [ObservableProperty]
    public partial string ResolutionSourceText { get; private set; } = NotSet;

    [ObservableProperty]
    public partial string PixelSizeText { get; private set; } = NotSet;

    [ObservableProperty]
    public partial string PixelSizeSourceText { get; private set; } = NotSet;

    /// <summary>Where the camera and the camera database say different things, one sentence each; empty when they agree or there is nothing to compare.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict))]
    public partial string ConflictText { get; private set; } = string.Empty;

    public bool HasConflict => ConflictText.Length > 0;

    private const string NotSet = "Not set";

    private void ShowResolved(OpticalTrainGeometry g)
    {
        CameraIdentityText = g.DatabaseEntry?.DisplayName ?? string.Empty;
        SensorNameText = g.SensorName ?? string.Empty;
        ResolutionText = g is { SensorWidthPixels: { } w, SensorHeightPixels: { } h } ? string.Create(CultureInfo.InvariantCulture, $"{w} × {h}") : NotSet;
        ResolutionSourceText = SourceName(Stronger(g.SensorWidthSource, g.SensorHeightSource));
        PixelSizeText = (g.PixelSizeXMicrons, g.PixelSizeYMicrons) switch
        {
            ({ } x, { } y) when Math.Abs(x - y) < 1e-9 => string.Create(CultureInfo.InvariantCulture, $"{x:0.##} µm"),
            ({ } x, { } y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.##} × {y:0.##} µm"),
            ({ } x, null) => string.Create(CultureInfo.InvariantCulture, $"{x:0.##} µm (width only)"),
            (null, { } y) => string.Create(CultureInfo.InvariantCulture, $"{y:0.##} µm (height only)"),
            _ => NotSet,
        };
        PixelSizeSourceText = SourceName(Stronger(g.PixelSizeXSource, g.PixelSizeYSource));
        ConflictText = ConflictsOf(g);
    }

    private static string ConflictsOf(OpticalTrainGeometry g)
    {
        var lines = new List<string>();
        var database = g.DatabaseEntry?.DisplayName ?? "the camera database";
        var size = g.Conflicts.Where(c => c.IsPixelSize).ToList();
        if (size.Count > 0)
        {
            var device = string.Join(" × ", size.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.DeviceValue:0.##}")));
            var db = string.Join(" × ", size.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.DatabaseValue:0.##}")));
            lines.Add($"Pixel size: the camera reports {device} µm; the Sidera camera database says {db} µm for {database}.");
        }

        var width = g.Conflicts.FirstOrDefault(c => c.Field == GeometryField.WidthPixels);
        var height = g.Conflicts.FirstOrDefault(c => c.Field == GeometryField.HeightPixels);
        if (width is not null || height is not null)
        {
            var device = string.Create(CultureInfo.InvariantCulture, $"{width?.DeviceValue ?? g.SensorWidthPixels:0} × {height?.DeviceValue ?? g.SensorHeightPixels:0}");
            var db = string.Create(CultureInfo.InvariantCulture, $"{width?.DatabaseValue ?? g.DatabaseEntry?.WidthPixels:0} × {height?.DatabaseValue ?? g.DatabaseEntry?.HeightPixels:0}");
            lines.Add($"Resolution: the camera reports {device}; the Sidera camera database says {db} for {database}.");
        }

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines) + " The camera's values are used.";
    }

    private static GeometrySource Stronger(GeometrySource a, GeometrySource b)
    {
        static int Rank(GeometrySource s) => s switch { GeometrySource.Configured => 3, GeometrySource.DeviceReported => 2, GeometrySource.Database => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private static string SourceName(GeometrySource source) => source switch
    {
        GeometrySource.Configured => "Manual",
        GeometrySource.DeviceReported => "Device",
        GeometrySource.Database => "Sidera Camera Database",
        _ => NotSet,
    };

    /// <summary>A pixel size or a sensor size is entered by hand, so the automatic values are overridden.</summary>
    public bool HasOverride =>
        PixelSizeXText.Trim().Length > 0 || PixelSizeYText.Trim().Length > 0 || SensorWidthText.Trim().Length > 0 || SensorHeightText.Trim().Length > 0;

    private bool CanRevert() => CanEdit && HasOverride;

    /// <summary>
    /// Clears the pixel size and the sensor size that were entered, so that the camera's own values (and, for a known camera, the database's) are used again. The focal length and the aperture
    /// stay. Saved at once when the rig already has optics.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void RevertToAutomatic()
    {
        _loading = true;
        PixelSizeXText = string.Empty;
        PixelSizeYText = string.Empty;
        SensorWidthText = string.Empty;
        SensorHeightText = string.Empty;
        _loading = false;
        Recompute();
        OnPropertyChanged(nameof(HasOverride));
        if (HasConfiguration)
        {
            Save();
        }
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
        GeometrySource.Database => "from the Sidera camera database",
        _ => "unknown",
    };

    public void Dispose() => _camera.Refreshed -= OnCameraRefreshed;
}
