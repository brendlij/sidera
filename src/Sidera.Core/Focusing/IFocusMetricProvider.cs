using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Core.Focusing;

/// <summary>What a focus metric is asked about: one exposure, and the optical train it was taken with.</summary>
/// <param name="Rig">The rig whose camera exposed the frame.</param>
/// <param name="FocuserPosition">Where the focuser stood while the frame was exposed.</param>
public sealed record FocusMetricInput(
    RigId Rig,
    DeviceId CameraId,
    DeviceId FocuserId,
    int FocuserPosition,
    CameraFrame Frame
);

/// <summary>
/// Turns an exposure into a focus measurement. The autofocus algorithm only knows this: whether the HFR comes from a
/// simulation or from an analysis of the stars in <see cref="FocusMetricInput.Frame"/> is for the implementation.
/// </summary>
public interface IFocusMetricProvider
{
    /// <summary>The measurement of <paramref name="input"/>, taken at the input's focuser position.</summary>
    /// <exception cref="InvalidOperationException">The frame cannot be measured (for example no stars).</exception>
    Task<FocusMeasurement> MeasureAsync(FocusMetricInput input, CancellationToken cancellationToken = default);
}
