using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;

namespace Sidera.Core.Framing;

/// <summary>Where the center of the current field comes from.</summary>
public enum CurrentViewSource
{
    /// <summary>Where the mount says it points: approximate, but live (it follows a slew).</summary>
    Mount,

    /// <summary>Where the latest plate solve found the camera: more accurate, and as current as the solve (the mount has not moved since).</summary>
    Solved,
}

/// <summary>
/// Where the rig points now, for drawing the current field on the sky next to the framing that is planned. The rotation is the one of the latest reliable plate solve, and <c>null</c> when there
/// is none: it is never guessed, so a field without a rotation is drawn as unknown.
/// </summary>
public sealed record CurrentView(CelestialCoordinates Center, double? RotationDegrees, CurrentViewSource Source);

/// <summary>
/// Decides what the current field is from what is known: the position the mount reports (live), and the latest plate solve of the rig. The solved position is the better one while it is
/// current (the mount points where it pointed when the solve was made); once the mount has moved, the mount's position is the only live one, and the rotation of the solve is kept because
/// slewing does not turn the camera. Pure: it reads nothing and moves nothing.
/// </summary>
public static class CurrentViewResolver
{
    /// <summary>The mount counts as not moved since a solve when it is within this many degrees (one arcminute) of where it was when the solve was made.</summary>
    public const double UnchangedDegrees = 1.0 / 60.0;

    /// <param name="mountNow">Where the connected mount points now; <c>null</c> when there is none or it does not say.</param>
    /// <param name="solved">The center and rotation of the latest successful plate solve of this rig; <c>null</c> when there is none.</param>
    /// <param name="mountAtSolve">Where the mount pointed when that solve was made (the hint of the solve); <c>null</c> when it was not known.</param>
    public static CurrentView? Resolve(
        CelestialCoordinates? mountNow, (CelestialCoordinates Center, double? RotationDegrees)? solved, CelestialCoordinates? mountAtSolve)
    {
        double? rotation = solved?.RotationDegrees is { } r && double.IsFinite(r) ? SkyMath.NormalizeRotationDegrees(r) : null;
        if (solved is not { } solve)
        {
            return mountNow is null ? null : new CurrentView(mountNow, null, CurrentViewSource.Mount);
        }

        if (mountNow is null)
        {
            return new CurrentView(solve.Center, rotation, CurrentViewSource.Solved);
        }

        var unchanged = mountAtSolve is { } then && SkyMath.AngularSeparationDegrees(then, mountNow) <= UnchangedDegrees;
        return unchanged
            ? new CurrentView(solve.Center, rotation, CurrentViewSource.Solved)
            : new CurrentView(mountNow, rotation, CurrentViewSource.Mount);
    }
}
