using Sidera.Core.Devices;
using Sidera.Core.Mounts;

namespace Sidera.Core.Astrometry;

/// <summary>What a plate solver can do. Backend-neutral: a backend sets the flags that are true for it, nothing here names one.</summary>
[Flags]
public enum PlateSolverCapabilities
{
    None = 0,

    /// <summary>It uses a position, a scale or a field of view as a hint and is faster for it.</summary>
    HintedSolve = 1,

    /// <summary>It can solve without any hint, anywhere on the sky.</summary>
    BlindSolve = 2,

    /// <summary>The result has the rotation of the image.</summary>
    Rotation = 4,

    /// <summary>The result has the pixel scale.</summary>
    PixelScale = 8,

    /// <summary>The result has the transformation between pixels and the sky.</summary>
    Wcs = 16,

    /// <summary>The result says whether the image is mirrored.</summary>
    Parity = 32,
}

/// <summary>How much an image is shrunk before it is solved: a choice of the solver, or one factor. A factor of 1 is the full resolution.</summary>
public sealed record DownsamplePolicy
{
    private DownsamplePolicy(bool isAuto, int factor)
    {
        IsAuto = isAuto;
        Factor = factor;
    }

    public static DownsamplePolicy Auto { get; } = new(true, 0);

    public static DownsamplePolicy None { get; } = new(false, 1);

    public bool IsAuto { get; }

    /// <summary>The factor when it is not automatic (1 to 8); 0 while <see cref="IsAuto"/>.</summary>
    public int Factor { get; }

    /// <exception cref="ArgumentOutOfRangeException">The factor is not from 1 to 8.</exception>
    public static DownsamplePolicy Of(int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(factor, 8);
        return new DownsamplePolicy(false, factor);
    }

    public override string ToString() => IsAuto ? "auto" : $"{Factor}x";
}

/// <summary>
/// The image to solve: the frame of a camera, and what is known about how it was taken. Nothing is derived from pixels here; a value that
/// is not known is <c>null</c>, and a solver must not make one up.
/// </summary>
/// <param name="Frame">The pixels. For a color camera they are the raw mosaic, as the camera delivered them.</param>
/// <param name="ObservedAt">When the exposure began.</param>
/// <param name="PixelSizeXMicrons">The size of a pixel of this frame (already multiplied by the binning).</param>
public sealed record PlateSolveImage(
    CameraFrame Frame,
    DateTimeOffset? ObservedAt = null,
    double? PixelSizeXMicrons = null,
    double? PixelSizeYMicrons = null)
{
    public int Width => Frame.Width;

    public int Height => Frame.Height;
}

/// <summary>
/// What a plate solver is asked: the image and what is known about where and how it was taken. Every hint is optional. A solver with a position and
/// a scale solves fast and near the position; with less it searches wider; with none it solves blind when it can and <see cref="BlindAllowed"/> says
/// so. The scale and the field of view are those of this image (its binning and its region included), not of the sensor.
/// </summary>
public sealed record PlateSolveRequest(PlateSolveImage Image)
{
    /// <summary>Where the telescope points about, from the mount.</summary>
    public CelestialCoordinates? ApproximateCenter { get; init; }

    /// <summary>Arcseconds per pixel across the image (X) and down it (Y).</summary>
    public double? PixelScaleXArcsecPerPixel { get; init; }

    public double? PixelScaleYArcsecPerPixel { get; init; }

    /// <summary>The angle the image covers in degrees: its width and its height.</summary>
    public double? FieldOfViewXDegrees { get; init; }

    public double? FieldOfViewYDegrees { get; init; }

    public double? FocalLengthMm { get; init; }

    /// <summary>How far from <see cref="ApproximateCenter"/> to look, in degrees; <c>null</c> lets the solver use its own default.</summary>
    public double? SearchRadiusDegrees { get; init; }

    public DownsamplePolicy Downsample { get; init; } = DownsamplePolicy.Auto;

    /// <summary>A solver may fall back to a search without hints when the hinted one finds nothing, and may solve without any when none is known.</summary>
    public bool BlindAllowed { get; init; }

    /// <summary>The time the whole solve, retries included, may take; <c>null</c> lets the solver use its own default.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Why a solve did not give a position.</summary>
public enum PlateSolveFailure
{
    None,

    /// <summary>The solver is not there (not installed, not configured).</summary>
    NotAvailable,

    /// <summary>The star catalog the solver needs is not there.</summary>
    NoDatabase,

    /// <summary>The solver looked and found no match.</summary>
    NoSolution,

    /// <summary>There were too few stars in the image.</summary>
    NotEnoughStars,

    /// <summary>The solver could not use the image.</summary>
    ImageError,

    /// <summary>The solve ran out of time.</summary>
    Timeout,

    /// <summary>What the solver said could not be understood.</summary>
    MalformedOutput,

    /// <summary>The request cannot be solved (no hint and blind not allowed, or no blind solving).</summary>
    NotSolvable,

    /// <summary>Anything else; the message says what.</summary>
    Error,
}

/// <summary>The transformation between the pixels of the solved image and the sky, in the conventions of FITS WCS (tangent projection).</summary>
/// <param name="ReferencePixelX">The reference pixel, counted from 1 at the left edge of the first pixel column, as in FITS.</param>
/// <param name="ReferencePixelY">Counted from 1 at the bottom row of the image as it is shown (the top of the frame is up).</param>
/// <param name="ReferenceRightAscensionDegrees">The sky position of the reference pixel: right ascension in degrees.</param>
/// <param name="Cd11">The matrix of the transformation in degrees per pixel: from pixel offsets (x right, y up) to offsets in right ascension (times cos of declination) and declination.</param>
public sealed record PlateSolveWcs(
    double ReferencePixelX,
    double ReferencePixelY,
    double ReferenceRightAscensionDegrees,
    double ReferenceDeclinationDegrees,
    double Cd11,
    double Cd12,
    double Cd21,
    double Cd22);

/// <summary>Whether the solved image is as the sky looks (east to the left when north is up) or mirrored.</summary>
public enum PlateSolveParity
{
    Normal,
    Mirrored,
}

/// <summary>One try of a solve: how it was set up and how it ended. A solve with retries lists them all, so nothing is hidden.</summary>
public sealed record PlateSolveAttempt(string Strategy, bool Succeeded, string Outcome, TimeSpan Duration, double? SearchRadiusDegrees, int? DownsampleFactor);

/// <summary>
/// What a plate solve came to. Backend-neutral: whatever the solver says is turned into these values, and a value it does not say is <c>null</c>,
/// never zero. Angles: <see cref="Center"/> is right ascension in hours and declination in degrees (as everywhere in Sidera);
/// <see cref="RotationDegrees"/> is the angle from the top of the image to celestial north, counterclockwise as the image is seen (the FITS
/// <c>CROTA2</c>), in (-180, 180]; scales are arcseconds per pixel; fields of view are degrees.
/// </summary>
public sealed record PlateSolveResult
{
    public bool Success { get; init; }

    public PlateSolveFailure Failure { get; init; }

    /// <summary>For a failure: what happened, in a sentence.</summary>
    public string? Message { get; init; }

    public CelestialCoordinates? Center { get; init; }

    public double? RotationDegrees { get; init; }

    public double? PixelScaleXArcsecPerPixel { get; init; }

    public double? PixelScaleYArcsecPerPixel { get; init; }

    /// <summary>The mean of the two scales (the geometric one); the single number to compare with a configured scale.</summary>
    public double? PixelScaleArcsecPerPixel =>
        PixelScaleXArcsecPerPixel is { } x && PixelScaleYArcsecPerPixel is { } y ? Math.Sqrt(x * y) : PixelScaleXArcsecPerPixel ?? PixelScaleYArcsecPerPixel;

    public double? FieldOfViewXDegrees { get; init; }

    public double? FieldOfViewYDegrees { get; init; }

    public PlateSolveParity? Parity { get; init; }

    public PlateSolveWcs? Wcs { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>The name of the solver that gave this result.</summary>
    public string Backend { get; init; } = string.Empty;

    public IReadOnlyList<PlateSolveAttempt> Attempts { get; init; } = [];

    /// <summary>A line or two for a person who wants to know more; never the raw output of the solver.</summary>
    public string? Diagnostic { get; init; }

    public static PlateSolveResult Failed(string backend, PlateSolveFailure failure, string message, TimeSpan duration, IReadOnlyList<PlateSolveAttempt>? attempts = null) =>
        new() { Success = false, Failure = failure, Message = message, Backend = backend, Duration = duration, Attempts = attempts ?? [] };
}

/// <summary>Whether a solver is ready, in lines a person can read ("ASTAP installed", "Star database: D50").</summary>
/// <param name="IsAvailable">A solve can be tried.</param>
/// <param name="Lines">What was found, one fact a line.</param>
/// <param name="Problem">When it is not available: what is missing, in a sentence.</param>
public sealed record PlateSolverStatus(bool IsAvailable, IReadOnlyList<string> Lines, string? Problem);

/// <summary>
/// A plate solver: finds where an image points. It describes astronomy (an image, a position, a scale) and no command line, so a solver
/// of any origin can stand behind it, and what uses it (a sequence step, centering, the solve page) never knows which one it has.
/// </summary>
public interface IPlateSolver
{
    /// <summary>The name of the solver for people: "ASTAP".</summary>
    string Name { get; }

    PlateSolverCapabilities Capabilities { get; }

    /// <summary>Whether the solver can be used now and what it found; looks at the installation, never at an image.</summary>
    Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Solves one image. A solve that finds nothing, or cannot run, is a result with <see cref="PlateSolveResult.Success"/> false and a reason, not
    /// an exception. Cancelling the token ends the solve (and anything the solver started) and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a <see cref="DownsamplePolicy"/> into a factor for a given image, the same way every time. Automatic means: the smallest of 1, 2 and 4 that leaves at most
/// <see cref="MaxSolvePixels"/> pixels (a 26-megapixel frame is solved at half size), never so much that the scale passes <see cref="MaxScaleArcsecPerPixel"/> (stars would
/// be too small) or that the short side drops under <see cref="MinShortSidePixels"/>.
/// </summary>
public static class DownsamplePlanner
{
    public const double MaxSolvePixels = 8_000_000;
    public const double MaxScaleArcsecPerPixel = 4.0;
    public const int MinShortSidePixels = 500;

    public static int Resolve(DownsamplePolicy policy, int width, int height, double? pixelScaleArcsecPerPixel)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsAuto)
        {
            return policy.Factor;
        }

        var factor = 4;
        foreach (var candidate in new[] { 1, 2, 4 })
        {
            if ((double)width * height / ((double)candidate * candidate) <= MaxSolvePixels)
            {
                factor = candidate;
                break;
            }
        }

        while (factor > 1 && pixelScaleArcsecPerPixel is { } scale && scale * factor > MaxScaleArcsecPerPixel)
        {
            factor /= 2;
        }

        while (factor > 1 && Math.Min(width, height) / factor < MinShortSidePixels)
        {
            factor /= 2;
        }

        return factor;
    }
}
