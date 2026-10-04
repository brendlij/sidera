namespace Sidera.Core.Guiding;

/// <summary>
/// One guide measurement, backend-neutral. The errors are the offset of the guide star from its lock position, split into the right
/// ascension and the declination axis of the mount, with the sign the guider reports. A value the guider did not report is
/// <c>null</c>, never zero.
/// </summary>
/// <param name="Time">When the sample was received.</param>
/// <param name="RaErrorPixels">Right ascension error, in pixels of the guide camera.</param>
/// <param name="DecErrorPixels">Declination error, in pixels of the guide camera.</param>
/// <param name="RaErrorArcsec">Right ascension error in arcseconds: the pixels times the pixel scale; <c>null</c> while the scale is not known.</param>
/// <param name="DecErrorArcsec">Declination error in arcseconds; <c>null</c> while the scale is not known.</param>
/// <param name="RaPulseMilliseconds">The guide pulse sent in right ascension, in milliseconds: positive east, negative west; <c>null</c> when there was none or it is not known.</param>
/// <param name="DecPulseMilliseconds">The guide pulse sent in declination, in milliseconds: positive north, negative south; <c>null</c> as above.</param>
/// <param name="StarSnr">Signal-to-noise ratio of the guide star.</param>
public sealed record GuidingSample(
    DateTimeOffset Time,
    double? RaErrorPixels,
    double? DecErrorPixels,
    double? RaErrorArcsec,
    double? DecErrorArcsec,
    double? RaPulseMilliseconds = null,
    double? DecPulseMilliseconds = null,
    double? StarSnr = null)
{
    /// <summary>The length of the error vector in arcseconds; <c>null</c> unless both axes are known in arcseconds.</summary>
    public double? TotalErrorArcsec => RaErrorArcsec is { } ra && DecErrorArcsec is { } dec ? Math.Sqrt(ra * ra + dec * dec) : null;

    /// <summary>The length of the error vector in pixels; <c>null</c> unless both axes are known.</summary>
    public double? TotalErrorPixels => RaErrorPixels is { } ra && DecErrorPixels is { } dec ? Math.Sqrt(ra * ra + dec * dec) : null;
}
