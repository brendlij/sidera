using System.Globalization;

namespace Sidera.Core.Devices;

/// <summary>What kind of sensor a camera has, as the driver reports it. Sidera does not debayer: this is information.</summary>
public enum SensorKind
{
    Unknown,
    Monochrome,
    Color,
    Rggb,
    Cmyg,
    Cmyg2,
    Lrgb,
}

/// <summary>
/// What a camera supports. Every member is read from standard capability flags or from members that were probed safely
/// after the connection was made; a member the driver does not offer is <c>null</c>, <c>false</c> or empty, and
/// <see cref="Notes"/> says why where the driver said something. Nothing here is inferred from the name of a driver or a
/// vendor.
/// </summary>
public sealed record CameraCapabilities
{
    public DriverMetadata Driver { get; init; } = new();

    // Exposure
    public bool CanAbortExposure { get; init; }
    public bool CanStopExposure { get; init; }
    public double? MinExposureSeconds { get; init; }
    public double? MaxExposureSeconds { get; init; }
    public double? ExposureResolutionSeconds { get; init; }

    // Sensor
    public int SensorWidth { get; init; }
    public int SensorHeight { get; init; }
    public double? PixelSizeXMicrons { get; init; }
    public double? PixelSizeYMicrons { get; init; }
    public int MaxAdu { get; init; }
    public double? ElectronsPerAdu { get; init; }
    public double? FullWellCapacity { get; init; }
    public SensorKind SensorType { get; init; }
    public string? SensorName { get; init; }

    /// <summary>The offset of the Bayer pattern, when the driver reports one (only meaningful for colour sensors).</summary>
    public (int X, int Y)? BayerOffset { get; init; }

    public bool HasShutter { get; init; }

    // Binning and subframe
    public int MaxBinX { get; init; } = 1;
    public int MaxBinY { get; init; } = 1;
    public bool CanAsymmetricBin { get; init; }
    public bool SupportsBinning => MaxBinX > 1 || MaxBinY > 1;

    /// <summary>The subframe (start and size) can be read and set. Standard for every camera of the interface.</summary>
    public bool SupportsSubframe { get; init; }

    // Gain and offset: a range of values, or a list of choices; none when the driver has neither
    public IntegerControl? Gain { get; init; }
    public IntegerControl? Offset { get; init; }

    // Readout
    public IReadOnlyList<string> ReadoutModes { get; init; } = [];
    public bool CanFastReadout { get; init; }

    // Cooling
    public bool CanSetCcdTemperature { get; init; }
    public bool HasCooler { get; init; }
    public bool CanGetCoolerPower { get; init; }
    public bool HasCcdTemperature { get; init; }
    public bool HasHeatSinkTemperature { get; init; }
    public bool SupportsCooling => CanSetCcdTemperature || HasCooler;

    /// <summary>What the probing of members that turned out not to be offered said, one line each, for the log and the details.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// The settings of a camera: the values that can be changed. As the current settings of a camera it holds what the device
/// reports (a value the device does not have is <c>null</c>); as a change it holds only what is to be set (<c>null</c>
/// leaves a value as it is). The subframe is in binned pixels, as the camera interface has it.
/// </summary>
public sealed record CameraSettings
{
    public int? Gain { get; init; }
    public int? Offset { get; init; }
    public int? BinX { get; init; }
    public int? BinY { get; init; }
    public int? StartX { get; init; }
    public int? StartY { get; init; }
    public int? NumX { get; init; }
    public int? NumY { get; init; }
    public int? ReadoutMode { get; init; }
    public bool? FastReadout { get; init; }

    /// <summary>The target temperature of the sensor in degrees Celsius.</summary>
    public double? TargetTemperature { get; init; }

    public bool? CoolerOn { get; init; }

    public bool IsEmpty => this == new CameraSettings();

    public bool ChangesSubframe => StartX is not null || StartY is not null || NumX is not null || NumY is not null;
}

/// <summary>What a camera is doing and measuring now. Values the camera does not report are <c>null</c>.</summary>
public sealed record CameraTelemetry
{
    public double? CcdTemperature { get; init; }
    public double? CoolerPower { get; init; }
    public double? HeatSinkTemperature { get; init; }
    public bool? CoolerOn { get; init; }

    /// <summary>How long the last exposure was, when the camera says.</summary>
    public double? LastExposureSeconds { get; init; }

    /// <summary>When the last exposure started, as the driver reports it (a text in the format the driver uses).</summary>
    public string? LastExposureStart { get; init; }

    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A camera that says what it supports and can be configured: gain, offset, binning, subframe, readout, cooling. The
/// operations are only as capable as <see cref="ICapable{T}.Capabilities"/> say, and refuse what is not. Nothing is
/// changed merely to find out what is supported.
/// </summary>
/// <summary>How the last exposure of a camera ended.</summary>
public enum CameraExposureOutcome
{
    /// <summary>No exposure has ended yet.</summary>
    None,

    /// <summary>The exposure ran its time and delivered its frame.</summary>
    Completed,

    /// <summary>The exposure was ended early on request and the camera delivered the image of what it had collected.</summary>
    Stopped,

    /// <summary>The exposure was thrown away: aborted on request or cancelled. No image was used.</summary>
    Aborted,

    /// <summary>The exposure failed: the camera or the driver reported a problem, or no image came.</summary>
    Failed,
}

/// <summary>
/// The exposure was stopped (<see cref="ICameraControl.StopExposureAsync"/>) and the camera delivered no image. The exposure is over
/// and the camera is idle; there is nothing to use.
/// </summary>
public sealed class CameraExposureStoppedException(string message) : Exception(message);

public interface ICameraControl : ICamera, ICapable<CameraCapabilities>, IObservableDevice
{
    /// <summary>How the last exposure ended; <see cref="CameraExposureOutcome.None"/> before the first one.</summary>
    CameraExposureOutcome LastOutcome { get; }

    /// <summary>
    /// Ends the running exposure early and keeps what the camera has collected: the pending <c>ExposeAsync</c> returns the frame of
    /// the shortened exposure (<see cref="FrameAcquisition.Stopped"/> says so). Not an abort: when the camera delivers no image the
    /// pending <c>ExposeAsync</c> fails with <see cref="CameraExposureStoppedException"/>. Only when the camera can
    /// (<see cref="CameraCapabilities.CanStopExposure"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">No exposure is running.</exception>
    /// <exception cref="Exception">The camera cannot stop an exposure and keep the image (an unsupported-operation error of its backend).</exception>
    Task StopExposureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws the running exposure away: the pending <c>ExposeAsync</c> ends with an <see cref="OperationCanceledException"/> and no image
    /// is used, whatever the camera does with it. Cancelling the token of <c>ExposeAsync</c> does the same. Only when the camera can
    /// (<see cref="CameraCapabilities.CanAbortExposure"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">No exposure is running.</exception>
    /// <exception cref="Exception">The camera cannot abort an exposure (an unsupported-operation error of its backend).</exception>
    Task AbortExposureAsync(CancellationToken cancellationToken = default);

    /// <summary>The settings the camera has now, as it reported them; <c>null</c> while not connected.</summary>
    CameraSettings? Settings { get; }

    /// <summary>What the camera measures now; <c>null</c> while not connected.</summary>
    CameraTelemetry? Telemetry { get; }

    /// <summary>
    /// Applies the settings that are set in <paramref name="change"/> and reads the settings back. The change is checked
    /// against the capabilities first and nothing is changed when any part is refused; a failure of the driver in the middle
    /// is reported with the settings that were read back.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not supported or out of range; the message says which.</exception>
    /// <exception cref="InvalidOperationException">The camera is not connected, or it is exposing and the setting cannot change now.</exception>
    Task ApplyAsync(CameraSettings change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <see cref="CameraExposureRequest.Change"/> and takes the exposure as one operation. When the change cannot be
    /// applied the exposure is not started, and what was already applied before the failing part is not undone: the camera keeps
    /// the settings it has, which <see cref="Settings"/> reports. The camera is left as configured afterwards; nothing is
    /// restored.
    /// </summary>
    /// <exception cref="ArgumentException">A setting is not supported or out of range.</exception>
    Task<CameraFrame> ExposeAsync(CameraExposureRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The rules for a change of camera settings, the same for every backend and for the form that asks for one.</summary>
public static class CameraSettingsRules
{
    /// <summary>What is wrong with <paramref name="change"/>, one sentence each; empty when it can be applied.</summary>
    public static IReadOnlyList<string> Problems(CameraCapabilities capabilities, CameraSettings current, CameraSettings change)
    {
        var problems = new List<string>();

        if (change.Gain is { } gain && (capabilities.Gain is null || !capabilities.Gain.Accepts(gain)))
        {
            problems.Add(capabilities.Gain is null ? "This camera has no gain setting." : $"Gain must be {Describe(capabilities.Gain)}.");
        }

        if (change.Offset is { } offset && (capabilities.Offset is null || !capabilities.Offset.Accepts(offset)))
        {
            problems.Add(capabilities.Offset is null ? "This camera has no offset setting." : $"Offset must be {Describe(capabilities.Offset)}.");
        }

        var binX = change.BinX ?? current.BinX ?? 1;
        var binY = change.BinY ?? current.BinY ?? 1;
        if (change.BinX is not null || change.BinY is not null)
        {
            if (binX < 1 || binY < 1 || binX > capabilities.MaxBinX || binY > capabilities.MaxBinY)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture, $"Binning must be from 1 to {capabilities.MaxBinX} horizontally and 1 to {capabilities.MaxBinY} vertically."));
            }
            else if (binX != binY && !capabilities.CanAsymmetricBin)
            {
                problems.Add("This camera only bins the same horizontally and vertically.");
            }
        }

        if (change.ChangesSubframe)
        {
            var startX = change.StartX ?? current.StartX ?? 0;
            var startY = change.StartY ?? current.StartY ?? 0;
            var numX = change.NumX ?? current.NumX ?? 0;
            var numY = change.NumY ?? current.NumY ?? 0;
            var width = capabilities.SensorWidth / Math.Max(1, binX);
            var height = capabilities.SensorHeight / Math.Max(1, binY);
            if (!capabilities.SupportsSubframe)
            {
                problems.Add("This camera has no subframe setting.");
            }
            else if (startX < 0 || startY < 0 || numX < 1 || numY < 1 || startX + numX > width || startY + numY > height)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture, $"The subframe must lie inside the {width} x {height} pixels of the sensor at this binning."));
            }
        }

        if (change.ReadoutMode is { } mode && (mode < 0 || mode >= capabilities.ReadoutModes.Count))
        {
            problems.Add(capabilities.ReadoutModes.Count == 0 ? "This camera has no readout modes." : "That readout mode does not exist.");
        }

        if (change.FastReadout is not null && !capabilities.CanFastReadout)
        {
            problems.Add("This camera has no fast readout.");
        }

        if (change.TargetTemperature is { } target)
        {
            if (!capabilities.CanSetCcdTemperature)
            {
                problems.Add("This camera cannot set a target temperature.");
            }
            else if (!double.IsFinite(target) || target < -273.15 || target > 100)
            {
                problems.Add("The target temperature must be a number from -273 to 100 degrees.");
            }
        }

        if (change.CoolerOn is not null && !capabilities.HasCooler)
        {
            problems.Add("This camera has no cooler to switch.");
        }

        return problems;
    }

    private static string Describe(IntegerControl control) => control.IsList
        ? $"one of the {control.Choices.Count} choices"
        : string.Create(CultureInfo.InvariantCulture, $"from {control.Minimum} to {control.Maximum}");
}
