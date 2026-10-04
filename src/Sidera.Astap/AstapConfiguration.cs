using Sidera.Core.Astrometry;

namespace Sidera.Astap;

/// <summary>
/// How ASTAP is used: where it is and what it searches with. Nothing here is the state of a solve. A path that is not set is looked for in the usual places;
/// a path that is set is the one that counts (and is reported as missing when it is, never silently replaced).
/// </summary>
public sealed record AstapConfiguration
{
    public const double DefaultSearchRadius = 10.0;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The ASTAP program; <c>null</c> to look in the usual places. The console variant (<c>astap_cli.exe</c>) next to <c>astap.exe</c> is preferred: it opens no window.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The folder of the star database; <c>null</c> for the folder of the program, where ASTAP keeps it unless told otherwise.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>The database to use when several are installed ("D50"); <c>null</c> lets Sidera choose among the ones that are there.</summary>
    public string? DatabaseAbbreviation { get; init; }

    /// <summary>The time a whole solve may take, retries included, when the request does not say.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>How far from the hinted position the first try looks, in degrees, when the request does not say.</summary>
    public double SearchRadiusDegrees { get; init; } = DefaultSearchRadius;

    public DownsamplePolicy Downsample { get; init; } = DownsamplePolicy.Auto;

    /// <summary>Keep the files of a solve (the image and what ASTAP wrote) for a look; they are deleted otherwise.</summary>
    public bool KeepDiagnosticFiles { get; init; }

    /// <summary>The sentence about what is wrong with the values, or <c>null</c>.</summary>
    public string? Problem() =>
        SearchRadiusDegrees is <= 0 or > 180 || !double.IsFinite(SearchRadiusDegrees) ? "The search radius must be from 0 to 180 degrees."
        : Timeout <= TimeSpan.Zero ? "The timeout must be longer than zero."
        : null;
}
