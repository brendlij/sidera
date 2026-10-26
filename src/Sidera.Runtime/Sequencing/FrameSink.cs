using Sidera.Core.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>What a session knows about an exposure that the camera does not: the target it belongs to.</summary>
public sealed record FrameLabel(string? TargetName);

/// <summary>
/// Where the frames of a session go after they were taken. An exposure asks the sink for a capture when it starts (so that what is only known then, the filter in the light path and where the
/// mount points, is read at that moment) and completes it with the frame. A sink never makes an exposure fail: what it does with a frame is its own business.
/// </summary>
public interface IFrameSink
{
    IFrameCapture Begin(DeviceId cameraId, FrameLabel? label);
}

/// <summary>One exposure that is being taken for an <see cref="IFrameSink"/>.</summary>
public interface IFrameCapture
{
    void Complete(CameraFrame frame);

    /// <summary>The exposure ended without a frame (cancelled or failed).</summary>
    void Abandon();
}
