using System.Globalization;

namespace Sidera.Astap;

/// <summary>What one run of ASTAP is asked: the image, where to put the answer and what is known. Astronomy values only; the command line is built from it.</summary>
/// <param name="ImagePath">The FITS file to solve.</param>
/// <param name="OutputBasePath">The path without extension for the files ASTAP writes (<c>.ini</c> and <c>.wcs</c>).</param>
/// <param name="SearchRadiusDegrees">How far around the position to search; 180 for the whole sky.</param>
/// <param name="RightAscensionHours">The hinted position; with <see cref="DeclinationDegrees"/>. Both or neither.</param>
/// <param name="FieldOfViewHeightDegrees">The height of the image in degrees; <c>null</c> lets ASTAP find it (the slower way).</param>
/// <param name="DownsampleFactor">The shrinking before the solve; 0 lets ASTAP choose.</param>
public sealed record AstapInvocation(
    string ImagePath,
    string OutputBasePath,
    double SearchRadiusDegrees,
    double? RightAscensionHours,
    double? DeclinationDegrees,
    double? FieldOfViewHeightDegrees,
    int DownsampleFactor,
    string? DatabaseDirectory,
    string? DatabaseAbbreviation);

/// <summary>Builds the arguments of astap_cli. The only place in Sidera that knows the options of ASTAP.</summary>
public static class AstapCommandBuilder
{
    /// <summary>The arguments, one string each, so that no quoting of a path with spaces is ever needed.</summary>
    public static IReadOnlyList<string> Build(AstapInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = new List<string>
        {
            "-f", invocation.ImagePath,
            "-o", invocation.OutputBasePath,
            "-r", Number(invocation.SearchRadiusDegrees),
        };

        // ASTAP wants the south pole distance, which is the declination plus 90 degrees; the position is a hint only when both are given.
        if (invocation.RightAscensionHours is { } ra && invocation.DeclinationDegrees is { } dec)
        {
            args.Add("-ra");
            args.Add(Number(ra));
            args.Add("-spd");
            args.Add(Number(dec + 90.0));
        }

        args.Add("-fov");
        args.Add(Number(invocation.FieldOfViewHeightDegrees ?? 0));
        args.Add("-z");
        args.Add(invocation.DownsampleFactor.ToString(CultureInfo.InvariantCulture));
        args.Add("-wcs");

        if (!string.IsNullOrWhiteSpace(invocation.DatabaseDirectory))
        {
            args.Add("-d");
            args.Add(invocation.DatabaseDirectory!);
        }

        if (!string.IsNullOrWhiteSpace(invocation.DatabaseAbbreviation))
        {
            args.Add("-D");
            args.Add(invocation.DatabaseAbbreviation!.ToLowerInvariant());
        }

        return args;
    }

    // A dot, whatever the language of the computer is.
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
