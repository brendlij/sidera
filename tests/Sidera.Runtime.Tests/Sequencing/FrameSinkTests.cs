using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

/// <summary>What an exposure gives to the sink of the frames: a capture at its start, the frame when it has one, and never a failed exposure because of what the sink does.</summary>
public class FrameSinkTests
{
    private sealed class RecordingSink : IFrameSink
    {
        public List<string> Events { get; } = [];
        public List<(DeviceId Camera, FrameLabel? Label)> Begun { get; } = [];
        public CameraFrame? Frame { get; private set; }
        public Exception? BeginThrows { get; set; }
        public Exception? CompleteThrows { get; set; }

        public IFrameCapture Begin(DeviceId cameraId, FrameLabel? label)
        {
            Events.Add("begin");
            if (BeginThrows is not null)
            {
                throw BeginThrows;
            }

            Begun.Add((cameraId, label));
            return new Capture(this);
        }

        private sealed class Capture(RecordingSink owner) : IFrameCapture
        {
            public void Complete(CameraFrame frame)
            {
                owner.Events.Add("complete");
                owner.Frame = frame;
                if (owner.CompleteThrows is not null)
                {
                    throw owner.CompleteThrows;
                }
            }

            public void Abandon() => owner.Events.Add("abandon");
        }
    }

    private static async Task<(DeviceRegistry Registry, SimulatedCamera Camera)> ConnectedCamera()
    {
        var registry = new DeviceRegistry();
        var camera = new SimulatedCamera(new DeviceId("camera.sink"), seed: 3);
        registry.Register(camera);
        await camera.ConnectAsync();
        return (registry, camera);
    }

    private static Task<SequenceStepResult> Run(CameraExposureAction action) => action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

    [Fact]
    public async Task AnExposure_BeginsACapture_BeforeItExposes_AndCompletesItWithTheFrameThatItReturns()
    {
        var (registry, camera) = await ConnectedCamera();
        var sink = new RecordingSink();
        var label = new FrameLabel("M31");

        var result = await Run(new CameraExposureAction(registry, camera.Id, TimeSpan.FromMilliseconds(20), frameSink: sink, label: label));

        Assert.Equal(["begin", "complete"], sink.Events);
        Assert.Equal((camera.Id, label), Assert.Single(sink.Begun));
        Assert.Same(result.Payload, sink.Frame);
    }

    [Fact]
    public async Task AnExposureThatFails_AbandonsTheCapture_AndFailsAsItDid()
    {
        var (registry, camera) = await ConnectedCamera();
        var sink = new RecordingSink();
        var action = new CameraExposureAction(
            registry, camera.Id, TimeSpan.FromMilliseconds(20), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(99999) }, frameSink: sink);

        await Assert.ThrowsAsync<AcquisitionException>(() => Run(action));

        Assert.Equal(["begin", "abandon"], sink.Events);
        Assert.Null(sink.Frame);
    }

    [Fact]
    public async Task AnExposureThatIsCancelled_AbandonsTheCapture()
    {
        var (registry, camera) = await ConnectedCamera();
        var sink = new RecordingSink();
        using var cts = new CancellationTokenSource();
        var action = new CameraExposureAction(registry, camera.Id, TimeSpan.FromSeconds(30), frameSink: sink);

        var running = action.ExecuteAsync(NoContext.Instance, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["begin", "abandon"], sink.Events);
    }

    [Fact]
    public async Task ASinkThatFails_NeverFailsTheExposure()
    {
        var (registry, camera) = await ConnectedCamera();
        var begins = new RecordingSink { BeginThrows = new IOException("no disk") };
        var completes = new RecordingSink { CompleteThrows = new IOException("disk full") };

        var first = await Run(new CameraExposureAction(registry, camera.Id, TimeSpan.FromMilliseconds(20), frameSink: begins));
        var second = await Run(new CameraExposureAction(registry, camera.Id, TimeSpan.FromMilliseconds(20), frameSink: completes));

        Assert.IsType<CameraFrame>(first.Payload);
        Assert.IsType<CameraFrame>(second.Payload);
    }

    [Fact]
    public async Task WithoutASink_NothingChanges()
    {
        var (registry, camera) = await ConnectedCamera();

        var result = await Run(new CameraExposureAction(registry, camera.Id, TimeSpan.FromMilliseconds(20)));

        Assert.IsType<CameraFrame>(result.Payload);
    }
}
