using System.Globalization;
using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;

namespace Sidera.Astap;

/// <summary>What ASTAP's answer came to, before it is turned into a <see cref="PlateSolveResult"/>.</summary>
public sealed record AstapOutcome
{
    public bool Solved { get; init; }
    public PlateSolveFailure Failure { get; init; }
    public string? Message { get; init; }
    public CelestialCoordinates? Center { get; init; }
    public double? RotationDegrees { get; init; }
    public double? PixelScaleXArcsecPerPixel { get; init; }
    public double? PixelScaleYArcsecPerPixel { get; init; }
    public double? FieldOfViewXDegrees { get; init; }
    public double? FieldOfViewYDegrees { get; init; }
    public PlateSolveParity? Parity { get; init; }
    public PlateSolveWcs? Wcs { get; init; }
}

/// <summary>
/// Reads what ASTAP writes: the exit code and the <c>.ini</c> file (<c>KEY=value</c> lines in FITS names: <c>PLTSOLVD</c>, <c>CRVAL1</c>, <c>CDELT1</c>, <c>CROTA2</c>, the
/// <c>CD</c> matrix). Numbers are read with the invariant culture and may have an exponent; nothing depends on the language of the computer. The only place in Sidera that
/// knows the files of ASTAP. A solution that lacks the values it needs is an error of the output, never a result with zeros.
/// </summary>
public static class AstapResultParser
{
    // The exit codes of astap_cli.
    public const int NoSolution = 1;
    public const int NotEnoughStars = 2;
    public const int ImageReadError = 16;
    public const int NoDatabase = 32;
    public const int DatabaseReadError = 33;
    public const int UpdateError = 34;

    /// <param name="iniText">The content of the <c>.ini</c> file, or <c>null</c> when ASTAP wrote none.</param>
    /// <param name="imageWidth">The width of the image that was solved, in pixels (of the original, not of a shrunken copy).</param>
    public static AstapOutcome Parse(int exitCode, string? iniText, int imageWidth, int imageHeight)
    {
        var values = ReadKeys(iniText);
        if (exitCode == 0 && (!values.TryGetValue("PLTSOLVD", out var status) || status is not ("T" or "F")))
            return Malformed("ASTAP returned no valid solve status (PLTSOLVD).");
        var solved = values.TryGetValue("PLTSOLVD", out var flag) && flag.Trim().StartsWith('T');

        if (exitCode != 0 || !solved)
        {
            return Failure(exitCode, values);
        }

        if (!TryNumber(values, "CRVAL1", out var raDegrees) || !TryNumber(values, "CRVAL2", out var decDegrees))
        {
            return Malformed("ASTAP reported a solution without a position (CRVAL1 and CRVAL2).");
        }

        if (!double.IsFinite(raDegrees) || !double.IsFinite(decDegrees) || decDegrees < -90 || decDegrees > 90)
        {
            return Malformed("ASTAP reported a position that is not on the sky.");
        }

        var haveCd = TryNumber(values, "CD1_1", out var cd11) & TryNumber(values, "CD1_2", out var cd12)
                     & TryNumber(values, "CD2_1", out var cd21) & TryNumber(values, "CD2_2", out var cd22);
        var haveCdelt = TryNumber(values, "CDELT1", out var cdelt1) & TryNumber(values, "CDELT2", out var cdelt2);
        var haveRota = TryNumber(values, "CROTA2", out var crota2) || TryNumber(values, "CROTA1", out crota2);

        double? scaleX = null, scaleY = null;
        if (haveCd)
        {
            scaleX = Math.Sqrt(cd11 * cd11 + cd21 * cd21) * SkyMath.ArcsecondsPerDegree;
            scaleY = Math.Sqrt(cd12 * cd12 + cd22 * cd22) * SkyMath.ArcsecondsPerDegree;
        }
        else if (haveCdelt)
        {
            scaleX = Math.Abs(cdelt1) * SkyMath.ArcsecondsPerDegree;
            scaleY = Math.Abs(cdelt2) * SkyMath.ArcsecondsPerDegree;
        }

        if (scaleX is not > 0 || scaleY is not > 0)
        {
            return Malformed("ASTAP reported a solution without a pixel scale (CD or CDELT).");
        }

        // The matrix, as FITS has it. Without a CD matrix it is built from CDELT and the rotation, as the standard says.
        if (!haveCd)
        {
            var rotationRadians = (haveRota ? crota2 : 0) * Math.PI / 180.0;
            cd11 = cdelt1 * Math.Cos(rotationRadians);
            cd12 = -cdelt2 * Math.Sin(rotationRadians);
            cd21 = cdelt1 * Math.Sin(rotationRadians);
            cd22 = cdelt2 * Math.Cos(rotationRadians);
        }

        var determinant = cd11 * cd22 - cd12 * cd21;
        if (!double.IsFinite(determinant) || determinant == 0)
            return Malformed("ASTAP reported a singular or invalid WCS transformation.");
        // East is to the left when north is up and the matrix turns: the determinant is negative. Positive means the image is mirrored.
        var parity = determinant < 0 ? PlateSolveParity.Normal : PlateSolveParity.Mirrored;
        var rotation = haveRota
            ? crota2
            : parity == PlateSolveParity.Normal ? Math.Atan2(-cd12, cd22) * 180.0 / Math.PI : Math.Atan2(cd21, cd11) * 180.0 / Math.PI;

        PlateSolveWcs? wcs = null;
        if (TryNumber(values, "CRPIX1", out var crpix1) && TryNumber(values, "CRPIX2", out var crpix2))
        {
            wcs = new PlateSolveWcs(crpix1, crpix2, raDegrees, decDegrees, cd11, cd12, cd21, cd22);
        }

        return new AstapOutcome
        {
            Solved = true,
            Center = SkyMath.FromDegrees(raDegrees, decDegrees),
            RotationDegrees = NormalizeRotation(rotation),
            PixelScaleXArcsecPerPixel = scaleX,
            PixelScaleYArcsecPerPixel = scaleY,
            FieldOfViewXDegrees = imageWidth > 0 ? scaleX * imageWidth / SkyMath.ArcsecondsPerDegree : null,
            FieldOfViewYDegrees = imageHeight > 0 ? scaleY * imageHeight / SkyMath.ArcsecondsPerDegree : null,
            Parity = parity,
            Wcs = wcs,
        };
    }

    /// <summary>An angle in degrees as (-180, 180].</summary>
    public static double NormalizeRotation(double degrees)
    {
        var wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
    }

    private static AstapOutcome Failure(int exitCode, Dictionary<string, string> values)
    {
        var said = values.TryGetValue("ERROR", out var error) && !string.IsNullOrWhiteSpace(error) ? error.Trim()
            : values.TryGetValue("WARNING", out var warning) && !string.IsNullOrWhiteSpace(warning) ? warning.Trim()
            : null;
        // Some CLI releases return exit 0 with PLTSOLVD=F and the failure only in ERROR.
        if (exitCode == 0 && said?.Contains("Not enough stars", StringComparison.OrdinalIgnoreCase) == true)
            return Fail(PlateSolveFailure.NotEnoughStars, "ASTAP did not find enough stars in the image.", said);
        return exitCode switch
        {
            NoSolution => Fail(PlateSolveFailure.NoSolution, "ASTAP found no solution.", said),
            NotEnoughStars => Fail(PlateSolveFailure.NotEnoughStars, "ASTAP did not find enough stars in the image.", said),
            ImageReadError => Fail(PlateSolveFailure.ImageError, "ASTAP could not read the image.", said),
            NoDatabase => Fail(PlateSolveFailure.NoDatabase, "ASTAP found no star database.", said),
            DatabaseReadError => Fail(PlateSolveFailure.NoDatabase, "ASTAP could not read the star database.", said),
            UpdateError => Fail(PlateSolveFailure.Error, "ASTAP could not write its solution.", said),
            // Exit 0 with a file that says it did not solve is a solve that found nothing.
            0 => Fail(PlateSolveFailure.NoSolution, "ASTAP found no solution.", said),
            _ => Fail(PlateSolveFailure.Error, string.Create(CultureInfo.InvariantCulture, $"ASTAP ended with the code {exitCode}."), said),
        };
    }

    private static AstapOutcome Fail(PlateSolveFailure failure, string message, string? said) =>
        new() { Failure = failure, Message = said is null ? message : $"{message} ({Shorten(said)})" };

    private static AstapOutcome Malformed(string message) => new() { Failure = PlateSolveFailure.MalformedOutput, Message = message };

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "...";

    private static Dictionary<string, string> ReadKeys(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return values;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            // A FITS style line may carry a comment after a slash; a quoted text keeps its slashes.
            if (!value.StartsWith('\'') && value.IndexOf('/') is var slash and > 0)
            {
                value = value[..slash].Trim();
            }

            values[key] = value.Trim('\'', ' ');
        }

        return values;
    }

    private static bool TryNumber(Dictionary<string, string> values, string key, out double number)
    {
        number = 0;
        return values.TryGetValue(key, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }
}
