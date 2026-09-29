namespace Astra.Core.Devices;

public interface ICamera : IDevice
{
    CameraExposureState ExposureState { get; }

    /// <summary>
    /// Duration of the running exposure, or of the last one if none is running.
    /// <c>null</c> if no exposure has been started yet.
    /// </summary>
    TimeSpan? ExposureDuration { get; }

    Task ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}
