using System.Globalization;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Core.Resources;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Dithers a guider that is already guiding; it never connects equipment, starts guiding or slews the mount.
/// <para>
/// A dither moves the mount, so it disturbs every camera on it. The action therefore first waits, through
/// <see cref="ISequenceStepContext.ExecuteWhenSafeAsync"/>, until the other branches of its coordination group are
/// at a safe point, and only then takes exclusive use of the guider, the mount and the affected cameras for as long
/// as the dither command runs. The order matters: taking a camera before its branch is at a safe point would wait
/// for that branch's exposure while the branch waits for the dither, a deadlock.
/// </para>
/// <para>
/// Nothing is inferred from the equipment: the sequence author lists the affected cameras, puts their branches in
/// the same coordination group, and places a <see cref="SafePointStep"/> after each of their exposures. Outside a
/// coordination group the dither runs as soon as its resources are free; the camera reservations still keep it from
/// overlapping exposures that go through the same resource manager. Concurrent dithers stay separate operations
/// that run one after another.
/// </para>
/// <para>
/// Completion means that the dither command has finished. It does not mean that guiding has settled or that it is
/// safe to expose.
/// </para>
/// </summary>
public sealed class DitherAction : ISequenceStep
{
    private readonly DitherCommand _command;

    /// <param name="amplitudePixels">Dither amplitude in guide camera pixels.</param>
    /// <param name="cameraIds">Every camera disturbed by the dither; copied, duplicates removed.</param>
    public DitherAction(
        DeviceRegistry registry,
        DeviceId guiderId,
        DeviceId mountId,
        IEnumerable<DeviceId> cameraIds,
        double amplitudePixels
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cameraIds);

        if (!double.IsFinite(amplitudePixels) || amplitudePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amplitudePixels), amplitudePixels, "Dither amplitude must be a finite, positive number of guider pixels.");
        }

        var cameras = cameraIds.Distinct().ToArray();
        if (cameras.Length == 0)
        {
            throw new ArgumentException("A dither needs at least one affected camera.", nameof(cameraIds));
        }

        GuiderId = guiderId;
        MountId = mountId;
        CameraIds = Array.AsReadOnly(cameras);
        AmplitudePixels = amplitudePixels;
        _command = new DitherCommand(registry, this);
    }

    public DeviceId GuiderId { get; }
    public DeviceId MountId { get; }

    /// <summary>The affected cameras, in the order given and without duplicates.</summary>
    public IReadOnlyList<DeviceId> CameraIds { get; }

    /// <summary>Dither amplitude in guide camera pixels.</summary>
    public double AmplitudePixels { get; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Dither {AmplitudePixels:0.##} px");

    /// <summary>Returns a result without payload once the dither command has finished.</summary>
    /// <exception cref="InvalidOperationException">
    /// A device is unknown or of the wrong kind, the guider cannot dither, or the equipment is not connected
    /// or not guiding.
    /// </exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        // Resources are acquired by the runner for the child, i.e. only once the group is safe.
        await context.ExecuteWhenSafeAsync(
            ct => context.ExecuteChildAsync(_command, 0, 1, ct),
            cancellationToken);
        return new SequenceStepResult();
    }

    // Holds the equipment while the dither runs. Executed by the runner, which acquires its resources first, so
    // it calls the devices directly and never goes through DeviceOperationService.
    private sealed class DitherCommand(DeviceRegistry registry, DitherAction definition) : IResourceAwareSequenceStep
    {
        public string Name => "Dither command";

        public IReadOnlyCollection<ResourceId> RequiredResources =>
            new[] { definition.GuiderId, definition.MountId }
                .Concat(definition.CameraIds)
                .Select(ResourceId.ForDevice)
                .Distinct()
                .ToArray();

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var guider = DeviceLookup.Resolve<IGuider>(registry, definition.GuiderId, "guider") as IDitherGuider
                ?? throw new InvalidOperationException($"Guider '{definition.GuiderId}' does not support dithering.");
            var mount = DeviceLookup.Resolve<IMount>(registry, definition.MountId, "mount");
            var cameras = definition.CameraIds
                .Select(id => DeviceLookup.Resolve<ICamera>(registry, id, "camera"))
                .ToArray();

            if (mount.ConnectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Mount '{mount.Id}' is not connected.");
            }

            foreach (var camera in cameras)
            {
                if (camera.ConnectionState != DeviceConnectionState.Connected)
                {
                    throw new InvalidOperationException($"Camera '{camera.Id}' is not connected.");
                }
            }

            if (guider.ConnectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Guider '{guider.Id}' is not connected.");
            }

            if (guider.GuidingState != GuidingState.Guiding)
            {
                throw new InvalidOperationException($"Guider '{guider.Id}' is not guiding.");
            }

            await guider.DitherAsync(definition.AmplitudePixels, cancellationToken);
            return new SequenceStepResult();
        }
    }
}
