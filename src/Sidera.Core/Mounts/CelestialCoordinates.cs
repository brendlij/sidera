namespace Sidera.Core.Mounts;

/// <summary>
/// A target on the sky as right ascension and declination. Plain immutable data: no epoch, no
/// precession and no conversions; whoever produces the values and whoever consumes them agree on the epoch.
/// </summary>
public sealed record CelestialCoordinates
{
    public CelestialCoordinates(double rightAscensionHours, double declinationDegrees)
    {
        if (!double.IsFinite(rightAscensionHours) || rightAscensionHours < 0 || rightAscensionHours >= 24)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rightAscensionHours),
                rightAscensionHours,
                "Right ascension must be a finite number from 0 (inclusive) to 24 (exclusive) hours.");
        }

        if (!double.IsFinite(declinationDegrees) || declinationDegrees < -90 || declinationDegrees > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declinationDegrees),
                declinationDegrees,
                "Declination must be a finite number from -90 to +90 degrees.");
        }

        RightAscensionHours = rightAscensionHours;
        DeclinationDegrees = declinationDegrees;
    }

    public double RightAscensionHours { get; }
    public double DeclinationDegrees { get; }
}
