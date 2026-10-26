using Sidera.Core.Events;

namespace Sidera.Core.Devices;

/// <summary>
/// The camera started or finished handing the image of an exposure over (the read of the image from the driver, which takes seconds for a large sensor). The exposure is not over until it has:
/// <see cref="CameraExposureState"/> stays Exposing, and this says that what is left of it is the download.
/// </summary>
public sealed record CameraDownloadStateChanged(DeviceId DeviceId, bool Downloading) : ISideraEvent;
