namespace Sidera.Core.Devices;

public interface ICamera : IDevice
{
    CameraExposureState ExposureState { get; }

    /// <summary>
    /// Duration of the running exposure, or of the last one if none is running.
    /// <c>null</c> if no exposure has been started yet.
    /// </summary>
    TimeSpan? ExposureDuration { get; }

    /// <summary>
    /// Time elapsed in the running exposure (the actual portion if it was cancelled).
    /// Zero when an exposure starts and before the first one.
    /// </summary>
    TimeSpan ExposureElapsed { get; }

    /// <summary>Elapsed portion of <see cref="ExposureDuration"/>, from 0.0 to 1.0.</summary>
    double ExposureProgress { get; }

    /// <summary>
    /// Raised roughly every 200 ms during an exposure, and when it starts and ends.
    /// May be raised on any thread. Deliberately local to the camera, not a domain event.
    /// </summary>
    event EventHandler? ExposureProgressChanged;

    /// <summary>
    /// Runs an exposure and returns the resulting frame. A cancelled exposure throws
    /// <see cref="OperationCanceledException"/> and produces no frame.
    /// </summary>
    Task<CameraFrame> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}
