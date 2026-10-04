namespace Sidera.Core.Guiding;

/// <summary>
/// A guider that can dither: shift its lock position by a small random offset so that successive exposures
/// land on slightly different pixels. Optional: guiders without dither support only implement <see cref="IGuider"/>.
/// </summary>
public interface IDitherGuider : IGuider
{
    /// <summary>
    /// Dithers by up to <paramref name="amplitudePixels"/>, measured in pixels of the guide camera (not of an
    /// imaging camera, and not in arcseconds). Requires a connected guider that is guiding; it never connects
    /// the guider or starts guiding.
    /// <para>
    /// Completion only means that the dither command has finished and guiding continues. It does not mean that
    /// guiding has settled at the new position, that the RMS is acceptable or that it is safe to expose.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amplitudePixels"/> is not a finite, positive number.</exception>
    Task DitherAsync(double amplitudePixels, CancellationToken cancellationToken = default);
}
