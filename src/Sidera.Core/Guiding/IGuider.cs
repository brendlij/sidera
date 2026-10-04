using Sidera.Core.Devices;

namespace Sidera.Core.Guiding;

/// <summary>
/// A device that guides. Connection and guiding state are independent dimensions: a guider is connected
/// (or not) and, while connected, guiding (or not). Neither operation connects the guider implicitly.
/// </summary>
public interface IGuider : IDevice
{
    /// <summary>
    /// The guiding lifecycle. <c>Guiding</c> reports an active guiding loop only,
    /// not acceptable RMS, successful calibration or readiness to expose.
    /// </summary>
    GuidingState GuidingState { get; }

    /// <summary>
    /// Starts guiding. Completes as soon as guiding has started; it does not stay pending while guiding runs.
    /// Guiding then stays active until it is stopped or the guider is disconnected.
    /// </summary>
    Task StartGuidingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops guiding. Completes when guiding has stopped.</summary>
    Task StopGuidingAsync(CancellationToken cancellationToken = default);
}
