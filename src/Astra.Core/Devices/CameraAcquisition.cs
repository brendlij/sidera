using System.Globalization;

namespace Astra.Core.Devices;

/// <summary>
/// What a frame is for, in terms of astrophotography and not of any driver. All four are made the same way for now, with
/// the shutter closed for a dark or a bias (a camera without a shutter refuses those, see <see cref="CameraCapabilities.HasShutter"/>),
/// but they stay different on purpose: calibration will treat them differently. A bias is not "an exposure of zero seconds".
/// </summary>
public enum FrameType
{
    Light,
    Dark,
    Flat,
    Bias,
}

/// <summary>
/// A gain or an offset as an exposure asks for it: a number, for a camera whose gain is a range, or the name of a choice, for
/// a camera whose gain is a list of named modes. Which of the two applies is decided by the camera that resolves it, never
/// here, and a document keeps the one that was entered.
/// </summary>
public sealed record AcquisitionLevel
{
    private AcquisitionLevel(int? number, string? name)
    {
        Number = number;
        Name = name;
    }

    public int? Number { get; }
    public string? Name { get; }

    public static AcquisitionLevel OfNumber(int number) => new(number, null);

    public static AcquisitionLevel OfName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AcquisitionLevel(null, name);
    }

    public override string ToString() => Name ?? Number!.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The part of the sensor to read, in binned pixels (the unit of the camera interface): the whole sensor, or a rectangle. The
/// whole sensor is a choice of its own and not a rectangle of the full size, so that it stays the whole sensor whatever the
/// binning is.
/// </summary>
public sealed record AcquisitionRegion
{
    private AcquisitionRegion(bool isFullFrame, int x, int y, int width, int height)
    {
        IsFullFrame = isFullFrame;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public bool IsFullFrame { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public static AcquisitionRegion Full { get; } = new(true, 0, 0, 0, 0);

    public static AcquisitionRegion Of(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        return new AcquisitionRegion(false, x, y, width, height);
    }

    public override string ToString() => IsFullFrame
        ? "full frame"
        : string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height} at {X},{Y}");
}

/// <summary>
/// What one exposure asks of the camera, besides its duration: the acquisition settings that belong to the image. Every
/// setting is optional, and not set means <i>inherit</i>: the camera's own default (kept in the equipment configuration),
/// and where it has none the camera as it is. Only the frame type is always there; it is a light frame unless said otherwise.
/// This is intent: it says what is wanted, not what the camera can do, and it is checked against the capabilities of the camera
/// that runs it, immediately before it runs.
/// </summary>
public sealed record AcquisitionIntent
{
    public static AcquisitionIntent Default { get; } = new();

    public FrameType FrameType { get; init; } = FrameType.Light;

    public AcquisitionLevel? Gain { get; init; }
    public AcquisitionLevel? Offset { get; init; }
    public int? BinX { get; init; }
    public int? BinY { get; init; }
    public AcquisitionRegion? Region { get; init; }

    /// <summary>The readout mode by its name, which stays valid when the list of modes changes; the index would not.</summary>
    public string? ReadoutMode { get; init; }

    public bool? FastReadout { get; init; }

    /// <summary>Nothing is asked for: a light frame with everything inherited.</summary>
    public bool IsDefault => this == Default;

    /// <summary>Something besides the frame type is set (the part that is a camera default when it is not).</summary>
    public bool HasOverrides => this with { FrameType = FrameType.Light } != Default;
}

/// <summary>Where the acquisition defaults of the cameras are kept (in the equipment configuration), seen from the runtime.</summary>
public interface IAcquisitionDefaultsSource
{
    /// <summary>The defaults of a camera: the settings it normally takes frames with. <c>null</c> when it has none.</summary>
    AcquisitionIntent? DefaultsFor(DeviceId cameraId);
}

/// <summary>
/// The acquisition settings a frame was really taken with, as far as they are known: what the camera reported after the settings
/// were applied. A value the camera does not have (no gain, no binning) is <c>null</c>; nothing is guessed.
/// </summary>
public sealed record FrameAcquisition
{
    public FrameType FrameType { get; init; } = FrameType.Light;
    public int? Gain { get; init; }

    /// <summary>The name of the gain, for a camera whose gain is a list.</summary>
    public string? GainName { get; init; }

    public int? Offset { get; init; }
    public string? OffsetName { get; init; }
    public int? BinX { get; init; }
    public int? BinY { get; init; }
    public int? StartX { get; init; }
    public int? StartY { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? ReadoutMode { get; init; }
    public bool? FastReadout { get; init; }
}

/// <summary>
/// An exposure for a camera that can apply acquisition settings: its duration, its frame type, and the settings to change
/// (only those that differ from what the camera has; empty when none do), resolved and checked already. The camera applies the
/// change and starts the exposure as one operation: when the change cannot be applied, no exposure is started.
/// </summary>
public sealed record CameraExposureRequest(TimeSpan Duration, FrameType FrameType, CameraSettings Change);

/// <summary>Whether an acquisition can be checked, and what it came to.</summary>
public enum AcquisitionStatus
{
    /// <summary>Checked against the capabilities of the connected camera, and fine.</summary>
    Valid,

    /// <summary>Checked, and something is not supported by the camera: <see cref="AcquisitionPlan.Problems"/> say what.</summary>
    Invalid,

    /// <summary>The camera is not connected, so its capabilities are not known: nothing can be said, and nothing is wrong yet.</summary>
    NotVerifiable,
}

/// <summary>
/// An intent resolved for one camera: what to change on it, what the frame will have been taken with, and what is wrong, if anything.
/// </summary>
public sealed record AcquisitionPlan(
    AcquisitionStatus Status,
    IReadOnlyList<string> Problems,
    CameraSettings Change,
    FrameAcquisition Effective,
    FrameType FrameType)
{
    public bool IsValid => Status == AcquisitionStatus.Valid;
}

/// <summary>
/// Resolves what an exposure asks for against the camera that takes it, the same for every backend. Each setting is, in this
/// order: what the exposure says, else the default of the camera, else what the camera has now. Nothing is coerced: a value that
/// the camera does not support is a problem that says which and why, and the exposure does not start.
/// </summary>
public static class AcquisitionResolver
{
    public static AcquisitionPlan Resolve(
        AcquisitionIntent intent,
        AcquisitionIntent? defaults,
        TimeSpan? duration,
        DeviceCapabilities<CameraCapabilities> capabilities,
        CameraSettings? current)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (capabilities.Value is not { } caps || current is null)
        {
            return new AcquisitionPlan(
                AcquisitionStatus.NotVerifiable, [], new CameraSettings(), new FrameAcquisition { FrameType = intent.FrameType }, intent.FrameType);
        }

        var problems = new List<string>();
        defaults ??= AcquisitionIntent.Default;

        // The origin of a value, for the sentence that says what is wrong with it.
        string Source(bool explicitValue) => explicitValue ? string.Empty : " (the camera default)";

        int? Level(string name, AcquisitionLevel? explicitLevel, AcquisitionLevel? defaultLevel, IntegerControl? control, int? now)
        {
            var level = explicitLevel ?? defaultLevel;
            if (level is null)
            {
                return now;
            }

            var source = Source(explicitLevel is not null);
            if (control is null)
            {
                problems.Add($"This camera has no {name} setting; {name} {level}{source} cannot be applied.");
                return now;
            }

            if (control.IsList)
            {
                if (level.Name is not { } wanted)
                {
                    problems.Add($"The {name} of this camera is a choice of {string.Join(", ", control.Choices)}; {level}{source} is a number.");
                    return now;
                }

                var index = control.Choices.ToList().FindIndex(choice => string.Equals(choice, wanted, StringComparison.Ordinal));
                if (index < 0)
                {
                    problems.Add($"The camera has no {name} '{wanted}'{source}; it offers {string.Join(", ", control.Choices)}.");
                    return now;
                }

                return index;
            }

            if (level.Number is not { } number)
            {
                problems.Add($"The {name} of this camera is a number from {control.Minimum} to {control.Maximum}; '{level}'{source} is a name.");
                return now;
            }

            if (!control.Accepts(number))
            {
                problems.Add($"The camera rejects {name} {number}{source}: it is from {control.Minimum} to {control.Maximum}.");
                return now;
            }

            return number;
        }

        var gain = Level("gain", intent.Gain, defaults.Gain, caps.Gain, current.Gain);
        var offset = Level("offset", intent.Offset, defaults.Offset, caps.Offset, current.Offset);

        // A binning that names one axis only means both axes for a camera that bins the same on both; a camera that can bin each axis
        // by itself keeps the other axis as it is.
        var askedX = intent.BinX ?? defaults.BinX;
        var askedY = intent.BinY ?? defaults.BinY;
        if (!caps.CanAsymmetricBin)
        {
            askedX ??= askedY;
            askedY ??= askedX;
        }

        var binX = askedX ?? current.BinX;
        var binY = askedY ?? current.BinY;
        if ((askedX is not null || askedY is not null) && !caps.SupportsBinning && (binX != 1 || binY != 1))
        {
            problems.Add($"This camera does not bin; {binX}x{binY} cannot be applied.");
        }

        // The region: what is said, else the default; a new binning without any region means the whole sensor at that binning.
        var regionIntent = intent.Region ?? defaults.Region;
        var regionSource = Source(intent.Region is not null);
        var binningChanges = (binX ?? 1) != (current.BinX ?? 1) || (binY ?? 1) != (current.BinY ?? 1);
        int? startX = current.StartX, startY = current.StartY, numX = current.NumX, numY = current.NumY;
        var effBinX = Math.Max(1, binX ?? 1);
        var effBinY = Math.Max(1, binY ?? 1);
        var width = caps.SensorWidth / effBinX;
        var height = caps.SensorHeight / effBinY;
        if (regionIntent is not null)
        {
            if (!caps.SupportsSubframe)
            {
                problems.Add($"This camera has no subframe setting; the region ({regionIntent}){regionSource} cannot be applied.");
            }
            else if (regionIntent.IsFullFrame)
            {
                (startX, startY, numX, numY) = (0, 0, width, height);
            }
            else
            {
                (startX, startY, numX, numY) = (regionIntent.X, regionIntent.Y, regionIntent.Width, regionIntent.Height);
                if (regionIntent.X + regionIntent.Width > width || regionIntent.Y + regionIntent.Height > height)
                {
                    problems.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The region {regionIntent}{regionSource} does not lie inside the {width} x {height} pixels of the sensor at binning {effBinX}x{effBinY}."));
                }
            }
        }
        else if (binningChanges && caps.SupportsSubframe)
        {
            (startX, startY, numX, numY) = (0, 0, width, height);
        }

        int? readout = current.ReadoutMode;
        var readoutName = intent.ReadoutMode ?? defaults.ReadoutMode;
        if (readoutName is not null)
        {
            var index = caps.ReadoutModes.ToList().FindIndex(mode => string.Equals(mode, readoutName, StringComparison.Ordinal));
            if (index < 0)
            {
                problems.Add(caps.ReadoutModes.Count == 0
                    ? $"This camera has no readout modes; '{readoutName}'{Source(intent.ReadoutMode is not null)} does not exist."
                    : $"The camera has no readout mode '{readoutName}'{Source(intent.ReadoutMode is not null)}; it offers {string.Join(", ", caps.ReadoutModes)}.");
            }
            else
            {
                readout = index;
            }
        }

        var fast = intent.FastReadout ?? defaults.FastReadout ?? current.FastReadout;
        if ((intent.FastReadout ?? defaults.FastReadout) is not null && !caps.CanFastReadout)
        {
            problems.Add("This camera has no fast readout.");
            fast = current.FastReadout;
        }

        if (intent.FrameType is FrameType.Dark or FrameType.Bias && !caps.HasShutter)
        {
            problems.Add($"This camera has no shutter, so Astra cannot take a {intent.FrameType.ToString().ToLowerInvariant()} frame with it.");
        }

        // A duration that is not positive is not an acquisition problem: the camera refuses it, as it always did.
        if (duration is { } seconds && seconds > TimeSpan.Zero)
        {
            if (caps.MinExposureSeconds is { } min && seconds.TotalSeconds < min)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"An exposure of {seconds.TotalSeconds:0.######} s is shorter than the {min:0.######} s this camera can do."));
            }

            if (caps.MaxExposureSeconds is { } max && seconds.TotalSeconds > max)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"An exposure of {seconds.TotalSeconds:0.###} s is longer than the {max:0.###} s this camera can do."));
            }
        }

        // What differs from the camera now; the rules of the camera settings check that as a whole.
        var change = new CameraSettings
        {
            Gain = gain != current.Gain ? gain : null,
            Offset = offset != current.Offset ? offset : null,
            BinX = binX != current.BinX ? binX : null,
            BinY = binY != current.BinY ? binY : null,
            StartX = startX != current.StartX ? startX : null,
            StartY = startY != current.StartY ? startY : null,
            NumX = numX != current.NumX ? numX : null,
            NumY = numY != current.NumY ? numY : null,
            ReadoutMode = readout != current.ReadoutMode ? readout : null,
            FastReadout = fast != current.FastReadout ? fast : null,
        };

        if (problems.Count == 0)
        {
            var binningFromDefault = intent.BinX is null && intent.BinY is null && (defaults.BinX is not null || defaults.BinY is not null);
            foreach (var problem in CameraSettingsRules.Problems(caps, current, change))
            {
                var binningProblem = problem.StartsWith("Binning", StringComparison.Ordinal) || problem.StartsWith("This camera only bins", StringComparison.Ordinal);
                problems.Add(binningFromDefault && binningProblem ? problem.TrimEnd('.') + " (the camera default)." : problem);
            }
        }

        string? Name(IntegerControl? control, int? value) =>
            control is { IsList: true } && value is { } v && v >= 0 && v < control.Choices.Count ? control.Choices[v] : null;

        var effective = new FrameAcquisition
        {
            FrameType = intent.FrameType,
            Gain = gain,
            GainName = Name(caps.Gain, gain),
            Offset = offset,
            OffsetName = Name(caps.Offset, offset),
            BinX = binX,
            BinY = binY,
            StartX = startX,
            StartY = startY,
            Width = numX,
            Height = numY,
            ReadoutMode = readout is { } r && r >= 0 && r < caps.ReadoutModes.Count ? caps.ReadoutModes[r] : null,
            FastReadout = fast,
        };

        return new AcquisitionPlan(
            problems.Count == 0 ? AcquisitionStatus.Valid : AcquisitionStatus.Invalid, problems, change, effective, intent.FrameType);
    }
}
