namespace Sidera.Core.Guiding;

/// <summary>
/// A guider that can tell when guiding has settled, for example after a dither. Optional: guiders without this
/// capability only implement <see cref="IGuider"/> (or <see cref="IDitherGuider"/>).
/// </summary>
public interface IGuidingSettler : IGuider
{
    /// <summary>
    /// Waits until the measured guide error meets <paramref name="options"/>: at or below the pixel threshold,
    /// continuously for the stable duration. Requires a connected guider that is guiding; it never connects the
    /// guider, starts guiding or changes the guiding state.
    /// <para>
    /// Completes normally only once the criterion has been met. The outcomes stay distinguishable:
    /// the criterion not met within the timeout → <see cref="GuidingSettleTimeoutException"/>;
    /// <paramref name="cancellationToken"/> cancelled → <see cref="OperationCanceledException"/>, and guiding
    /// itself continues; guiding stopped or the guider disconnected while waiting → <see cref="InvalidOperationException"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The guider is not connected or not guiding, or stopped guiding while waiting.</exception>
    /// <exception cref="GuidingSettleTimeoutException">Guiding did not settle within the timeout.</exception>
    Task SettleAsync(GuidingSettleOptions options, CancellationToken cancellationToken = default);
}
