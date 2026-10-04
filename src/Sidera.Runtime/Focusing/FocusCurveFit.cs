using Sidera.Core.Focusing;

namespace Sidera.Runtime.Focusing;

/// <summary>What a fit of focus samples says.</summary>
/// <param name="Vertex">The focuser position of the minimum of the curve.</param>
/// <param name="FittedHfr">The HFR the curve has there (not measured).</param>
/// <param name="RSquared">How much of the variation of the samples the curve explains, 0 to 1.</param>
internal sealed record FocusFit(double Vertex, double FittedHfr, double RSquared);

/// <summary>
/// Fits the focus curve. Near focus the HFR of a star is a hyperbola in the focuser position, so its square is a
/// parabola: a least-squares parabola through (position, HFR²) is exact for such a curve and needs no iteration. The
/// positions are centred and scaled for the fit, so it stays well conditioned with positions of tens of thousands.
/// <para>
/// A fit is only returned when there is something to rely on: enough samples, a curve that is not flat, a parabola
/// that opens upwards (a minimum, not a maximum), and one that explains the samples.
/// </para>
/// </summary>
internal static class FocusCurveFit
{
    /// <summary>The HFR has to differ at least this much (relative to the lowest) for there to be a curve.</summary>
    public const double MinimumRelativeSpread = 0.05;

    /// <summary>The parabola has to explain at least this much of the variation of the samples.</summary>
    public const double MinimumRSquared = 0.8;

    /// <returns>The fit, or <c>null</c> when the samples show no reliable minimum.</returns>
    public static FocusFit? Fit(IReadOnlyList<FocusMeasurement> samples)
    {
        if (samples.Count < AutofocusOptions.MinimumSampleCount || samples.Select(s => s.FocuserPosition).Distinct().Count() < samples.Count)
        {
            return null;
        }

        var lowest = samples.Min(s => s.Hfr);
        var highest = samples.Max(s => s.Hfr);
        if ((highest - lowest) / lowest < MinimumRelativeSpread)
        {
            return null; // flat: nothing to tell where focus is
        }

        var mean = samples.Average(s => (double)s.FocuserPosition);
        var scale = samples.Max(s => Math.Abs(s.FocuserPosition - mean));
        if (scale <= 0)
        {
            return null;
        }

        // Normal equations of y = c0 + c1 u + c2 u², with u the scaled position and y = HFR².
        double s0 = samples.Count, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        foreach (var sample in samples)
        {
            var u = (sample.FocuserPosition - mean) / scale;
            var y = sample.Hfr * sample.Hfr;
            s1 += u;
            s2 += u * u;
            s3 += u * u * u;
            s4 += u * u * u * u;
            t0 += y;
            t1 += u * y;
            t2 += u * u * y;
        }

        if (!Solve(
                [[s0, s1, s2], [s1, s2, s3], [s2, s3, s4]],
                [t0, t1, t2],
                out var c))
        {
            return null;
        }

        // A minimum needs a parabola that opens upwards.
        if (!double.IsFinite(c[0] + c[1] + c[2]) || c[2] <= 0)
        {
            return null;
        }

        var meanY = t0 / samples.Count;
        double residual = 0, total = 0;
        foreach (var sample in samples)
        {
            var u = (sample.FocuserPosition - mean) / scale;
            var y = sample.Hfr * sample.Hfr;
            var predicted = c[0] + c[1] * u + c[2] * u * u;
            residual += (y - predicted) * (y - predicted);
            total += (y - meanY) * (y - meanY);
        }

        var rSquared = total > 0 ? 1 - residual / total : 0;
        if (rSquared < MinimumRSquared)
        {
            return null;
        }

        var vertexU = -c[1] / (2 * c[2]);
        var vertex = mean + vertexU * scale;
        var minimum = c[0] - c[1] * c[1] / (4 * c[2]);
        if (!double.IsFinite(vertex))
        {
            return null;
        }

        // The parabola can dip below zero where there are no samples; no star has an HFR of that. Then the lowest
        // sample is the best that was seen.
        return new FocusFit(vertex, minimum > 0 ? Math.Sqrt(minimum) : lowest, rSquared);
    }

    // Gaussian elimination with partial pivoting; false for a matrix that cannot be solved.
    private static bool Solve(double[][] a, double[] b, out double[] x)
    {
        var n = b.Length;
        x = new double[n];
        for (var column = 0; column < n; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row][column]) > Math.Abs(a[pivot][column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(a[pivot][column]) < 1e-12)
            {
                return false;
            }

            (a[column], a[pivot]) = (a[pivot], a[column]);
            (b[column], b[pivot]) = (b[pivot], b[column]);

            for (var row = column + 1; row < n; row++)
            {
                var factor = a[row][column] / a[column][column];
                for (var k = column; k < n; k++)
                {
                    a[row][k] -= factor * a[column][k];
                }

                b[row] -= factor * b[column];
            }
        }

        for (var row = n - 1; row >= 0; row--)
        {
            var sum = b[row];
            for (var k = row + 1; k < n; k++)
            {
                sum -= a[row][k] * x[k];
            }

            x[row] = sum / a[row][row];
        }

        return true;
    }
}
