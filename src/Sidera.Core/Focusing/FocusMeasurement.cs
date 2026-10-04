namespace Sidera.Core.Focusing;

/// <summary>
/// How well focused one exposure was, at the focuser position it was taken at. The metric is the half flux radius
/// (HFR) of the stars, in pixels: lower is better. Where the number comes from (a simulation, later an analysis of
/// the pixels of the frame) is not part of it.
/// </summary>
public sealed record FocusMeasurement
{
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hfr"/> is not a finite, positive number.</exception>
    public FocusMeasurement(int focuserPosition, double hfr)
    {
        if (!double.IsFinite(hfr) || hfr <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hfr), hfr, "HFR must be a finite number greater than 0.");
        }

        FocuserPosition = focuserPosition;
        Hfr = hfr;
    }

    /// <summary>The absolute focuser position, in steps, at which the frame was exposed.</summary>
    public int FocuserPosition { get; }

    /// <summary>Half flux radius in pixels; lower is better.</summary>
    public double Hfr { get; }
}
