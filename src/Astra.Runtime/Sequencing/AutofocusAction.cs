using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Focusing;
using Astra.Core.Focusers;
using Astra.Core.Resources;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;
using Astra.Runtime.Focusing;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Focuses one rig: samples the focus at positions around the current one, fits the curve, and moves the focuser of
/// the rig to the best position (see <see cref="AutofocusEngine"/>). It uses the camera and the focuser that are
/// connected; it never connects them, never touches the filter wheel (the filter in the light path is the one that is
/// focused with) and never moves the mount.
/// <para>
/// It needs the camera and the focuser of the rig for the whole run, and only those: the runner takes both before the
/// run starts and gives both back when it ends, so nothing else can expose with that camera or move that focuser in
/// between, while every other rig carries on. The whole run is one step: it is not interrupted by a pause (which
/// takes effect after it) nor by anything coordinating other branches (a dither waits for it).
/// </para>
/// <para>
/// Cancelling stops the exposure or the move that is running and leaves the focuser where it last arrived.
/// </para>
/// </summary>
public sealed class AutofocusAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly IFocusMetricProvider _metrics;
    private readonly IEventPublisher? _events;

    /// <param name="events">Where the progress of a run is published; none when nobody listens.</param>
    public AutofocusAction(
        DeviceRegistry registry,
        RigId rigId,
        DeviceId cameraId,
        DeviceId focuserId,
        AutofocusOptions options,
        IFocusMetricProvider metrics,
        IEventPublisher? events = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        options.Validate();

        _registry = registry;
        _metrics = metrics;
        _events = events;
        RigId = rigId;
        CameraId = cameraId;
        FocuserId = focuserId;
        Options = options;
    }

    /// <summary>The action for a rig: its camera and its focuser.</summary>
    /// <exception cref="InvalidOperationException">The rig has no focuser.</exception>
    public static AutofocusAction ForRig(
        DeviceRegistry registry, Rig rig, AutofocusOptions options, IFocusMetricProvider metrics, IEventPublisher? events = null)
    {
        ArgumentNullException.ThrowIfNull(rig);

        return rig.FocuserId is { } focuserId
            ? new AutofocusAction(registry, rig.Id, rig.CameraId, focuserId, options, metrics, events)
            : throw new InvalidOperationException($"The rig '{rig.Id}' has no focuser, so it cannot be focused.");
    }

    public RigId RigId { get; }
    public DeviceId CameraId { get; }
    public DeviceId FocuserId { get; }
    public AutofocusOptions Options { get; }

    public string Name => "Autofocus";

    public IReadOnlyCollection<ResourceId> RequiredResources =>
        [ResourceId.ForDevice(CameraId), ResourceId.ForDevice(FocuserId)];

    /// <summary>Returns a result whose payload is the <see cref="AutofocusResult"/> of this execution.</summary>
    /// <exception cref="AutofocusFailedException">No reliable focus was found.</exception>
    /// <exception cref="InvalidOperationException">A device is unknown, of the wrong kind or not connected, or a measurement failed.</exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        try
        {
            var focuser = DeviceLookup.Resolve<IFocuser>(_registry, FocuserId, "focuser");
            var camera = DeviceLookup.Resolve<ICamera>(_registry, CameraId, "camera");

            // Before anything moves: a run that cannot measure must not start by moving the focuser.
            if (focuser.ConnectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Focuser '{FocuserId}' is not connected.");
            }

            if (camera.ConnectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Camera '{CameraId}' is not connected.");
            }

            var measurer = new FocusMeasurementOperation(_registry, RigId, CameraId, FocuserId, _metrics);
            var result = await AutofocusEngine.RunAsync(focuser, measurer, Options, Publish, cancellationToken);
            return new SequenceStepResult(result);
        }
        catch
        {
            // Cancelled or failed: whatever listens is told the run is over, even though the token is cancelled.
            await Publish(new AutofocusProgress(AutofocusPhase.Stopped, 0, 0, 0), CancellationToken.None);
            throw;
        }
    }

    private Task Publish(AutofocusProgress progress, CancellationToken cancellationToken) =>
        _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new AutofocusProgressChanged(RigId, progress), cancellationToken);
}
