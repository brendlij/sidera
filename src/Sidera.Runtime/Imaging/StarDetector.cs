using Sidera.Core.Devices;
using Sidera.Core.Imaging;

namespace Sidera.Runtime.Imaging;

/// <summary>
/// A classical, deterministic star detector.
/// <list type="number">
/// <item>
/// <description>
/// Threshold: a pixel is bright when it is above <c>background + DetectionSigma · noise</c> (the noise is never taken as
/// less than one ADU, so that a perfectly flat frame has no stars).
/// </description>
/// </item>
/// <item>
/// <description>
/// Candidates are the 8-connected regions of bright pixels, found with an explicit stack (no recursion, so a huge region
/// cannot overflow it) in one scan of the frame. A region of fewer than <c>MinStarPixels</c> pixels is a hot pixel or a
/// cosmic ray and is no star; one whose equivalent radius is above <c>MaxStarRadius</c> is no star either.
/// </description>
/// </item>
/// <item>
/// <description>
/// A candidate is measured in two passes on the background-subtracted pixels (negative values count as zero): a
/// first region from the size of the bright part gives a centroid and an HFR, and a second, wider one (about 3.5
/// times that HFR, at most <c>MaxStarRadius</c>) gives the final flux-weighted centroid, flux, HFR and shape. The wider
/// region holds the wings of the star that the threshold left out.
/// </description>
/// </item>
/// <item>
/// <description>
/// The HFR is the radius around the centroid that holds half of the flux: the pixels of the region are ordered by distance,
/// their flux added up, and the radius at which half is reached is interpolated between the two pixels around it.
/// </description>
/// </item>
/// </list>
/// A star is flagged, not dropped, when it is saturated, clipped by the edge or elongated: it is a star, but it is not
/// used to judge focus. The brightest <c>MaxStars</c> are kept, ordered by flux (then by position), so the result is the
/// same every time.
/// </summary>
public sealed class StarDetector : IStarDetector
{
    private const int RowsPerCancellationCheck = 32;

    public IReadOnlyList<DetectedStar> Detect(
        CameraFrame frame, FrameStatistics statistics, FrameAnalysisOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var width = frame.Width;
        var height = frame.Height;
        var pixels = frame.Pixels.Span;
        var background = statistics.Background;
        var threshold = background + options.DetectionSigma * Math.Max(statistics.BackgroundSigma, 1.0);

        var visited = new bool[pixels.Length];
        var stack = new List<int>(256);
        var region = new List<int>(256);
        var stars = new List<DetectedStar>();

        for (var y = 0; y < height; y++)
        {
            if (y % RowsPerCancellationCheck == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (visited[index] || pixels[index] <= threshold)
                {
                    continue;
                }

                // One bright region: every pixel of it is marked, so that it is found once.
                region.Clear();
                stack.Clear();
                stack.Add(index);
                visited[index] = true;
                var peak = 0;
                var saturated = false;
                while (stack.Count > 0)
                {
                    var current = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    region.Add(current);
                    var value = pixels[current];
                    peak = Math.Max(peak, value);
                    saturated |= value >= options.SaturationLevel;

                    var cx = current % width;
                    var cy = current / width;
                    for (var ny = Math.Max(0, cy - 1); ny <= Math.Min(height - 1, cy + 1); ny++)
                    {
                        for (var nx = Math.Max(0, cx - 1); nx <= Math.Min(width - 1, cx + 1); nx++)
                        {
                            var neighbour = ny * width + nx;
                            if (!visited[neighbour] && pixels[neighbour] > threshold)
                            {
                                visited[neighbour] = true;
                                stack.Add(neighbour);
                            }
                        }
                    }
                }

                if (region.Count < options.MinStarPixels)
                {
                    continue; // a hot pixel, a cosmic ray
                }

                var equivalentRadius = Math.Sqrt(region.Count / Math.PI);
                if (equivalentRadius > options.MaxStarRadius)
                {
                    continue; // far too big for a star
                }

                if (Measure(pixels, width, height, region, equivalentRadius, peak, saturated, background, options) is { } star)
                {
                    stars.Add(star);
                }
            }
        }

        // The brightest first, and in a fixed order for equal flux.
        stars.Sort((a, b) =>
        {
            var byFlux = b.Flux.CompareTo(a.Flux);
            if (byFlux != 0)
            {
                return byFlux;
            }

            var byY = a.Y.CompareTo(b.Y);
            return byY != 0 ? byY : a.X.CompareTo(b.X);
        });

        return stars.Count > options.MaxStars ? stars.GetRange(0, options.MaxStars) : stars;
    }

    private static DetectedStar? Measure(
        ReadOnlySpan<ushort> pixels,
        int width,
        int height,
        List<int> region,
        double equivalentRadius,
        int peak,
        bool saturated,
        double background,
        FrameAnalysisOptions options)
    {
        // The centre to start from: the flux-weighted centre of the bright part itself.
        double sx = 0, sy = 0, sf = 0;
        foreach (var index in region)
        {
            var flux = Math.Max(0, pixels[index] - background);
            sx += (index % width) * flux;
            sy += (index / width) * flux;
            sf += flux;
        }

        if (sf <= 0)
        {
            return null;
        }

        var cx = sx / sf;
        var cy = sy / sf;

        // First region, from the size of the bright part; then a wider one from what that says about the star.
        var firstRadius = Math.Clamp((int)Math.Ceiling(2 * equivalentRadius) + 2, 3, options.MaxStarRadius);
        if (!Profile(pixels, width, height, cx, cy, firstRadius, background, out var firstHfr, out cx, out cy, out _, out _))
        {
            return null;
        }

        var radius = Math.Clamp((int)Math.Ceiling(3.5 * firstHfr), 3, options.MaxStarRadius);
        if (!Profile(pixels, width, height, cx, cy, radius, background, out var hfr, out cx, out cy, out var totalFlux, out var elongation))
        {
            return null;
        }

        if (!double.IsFinite(hfr) || hfr <= 0 || !double.IsFinite(cx) || !double.IsFinite(cy) || !(totalFlux > 0))
        {
            return null;
        }

        // The measured region must lie inside the frame, with a margin: otherwise the wings of the star are cut off.
        var margin = options.EdgeMargin;
        var touchesEdge = cx - radius < margin || cx + radius > width - 1 - margin
                          || cy - radius < margin || cy + radius > height - 1 - margin;

        return new DetectedStar(
            cx, cy, peak, totalFlux, background, region.Count, radius, hfr, elongation, saturated, touchesEdge);
    }

    // Measures the star in a circular region: the flux-weighted centroid, the half flux radius, the flux and the
    // elongation. The region is clipped by the frame, not moved: pixels beyond it are not there.
    private static bool Profile(
        ReadOnlySpan<ushort> pixels,
        int width,
        int height,
        double centerX,
        double centerY,
        int radius,
        double background,
        out double hfr,
        out double x,
        out double y,
        out double totalFlux,
        out double elongation)
    {
        hfr = x = y = totalFlux = 0;
        elongation = double.PositiveInfinity;

        var left = Math.Max(0, (int)Math.Floor(centerX - radius));
        var right = Math.Min(width - 1, (int)Math.Ceiling(centerX + radius));
        var top = Math.Max(0, (int)Math.Floor(centerY - radius));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(centerY + radius));
        var radiusSquared = (double)radius * radius;

        // The centroid of the region around the given centre.
        double sx = 0, sy = 0, sf = 0;
        for (var py = top; py <= bottom; py++)
        {
            for (var px = left; px <= right; px++)
            {
                var dx = px - centerX;
                var dy = py - centerY;
                if (dx * dx + dy * dy > radiusSquared)
                {
                    continue;
                }

                var flux = Math.Max(0, pixels[py * width + px] - background);
                sx += px * flux;
                sy += py * flux;
                sf += flux;
            }
        }

        if (!(sf > 0))
        {
            return false;
        }

        x = sx / sf;
        y = sy / sf;

        // Pixels by their distance from that centroid, with their flux and the second moments of the star.
        var distances = new List<(double Distance, double Flux)>((2 * radius + 1) * (2 * radius + 1));
        double mxx = 0, myy = 0, mxy = 0;
        for (var py = top; py <= bottom; py++)
        {
            for (var px = left; px <= right; px++)
            {
                var dx = px - x;
                var dy = py - y;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > radiusSquared)
                {
                    continue;
                }

                var flux = Math.Max(0, pixels[py * width + px] - background);
                if (flux <= 0)
                {
                    continue;
                }

                distances.Add((Math.Sqrt(distanceSquared), flux));
                mxx += dx * dx * flux;
                myy += dy * dy * flux;
                mxy += dx * dy * flux;
            }
        }

        totalFlux = distances.Sum(d => d.Flux);
        if (!(totalFlux > 0))
        {
            return false;
        }

        distances.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var half = totalFlux / 2;
        double cumulative = 0;
        var previousDistance = 0.0;
        hfr = distances[^1].Distance;
        foreach (var (distance, flux) in distances)
        {
            if (cumulative + flux >= half)
            {
                // Between the pixel before and this one: where the running flux crosses half.
                hfr = previousDistance + (half - cumulative) / flux * (distance - previousDistance);
                break;
            }

            cumulative += flux;
            previousDistance = distance;
        }

        // The long and the short axis of the star, from its second moments.
        mxx /= totalFlux;
        myy /= totalFlux;
        mxy /= totalFlux;
        var mean = (mxx + myy) / 2;
        var spread = Math.Sqrt(((mxx - myy) / 2) * ((mxx - myy) / 2) + mxy * mxy);
        var major = mean + spread;
        var minor = mean - spread;
        elongation = minor > 1e-9 ? Math.Sqrt(major / minor) : double.PositiveInfinity;
        return true;
    }
}
