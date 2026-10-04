using System.Diagnostics;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Diagnostics;
using Sidera.Runtime.Focusing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.Sequencing;

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
/// <para>
/// Diagnostics: start and result (best position, HFR, samples, passes, duration) are Information, each measurement
/// (position and HFR) is Debug, a failure is an Error with its exception, a cancellation is Information. The entries carry
/// the rig as scope.
/// </para>
/// </summary>
public sealed class AutofocusAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly IFocusMetricProvider _metrics;
    private readonly IEventPublisher? _events;
    private readonly ILogger _logger;
    private readonly IAcquisitionDefaultsSource? _acquisitionDefaults;

    /// <param name="acquisitionDefaults">Where the defaults of the camera (gain, offset, readout) are read from; the binning and the region are always the whole sensor, unbinned.</param>
    /// <param name="events">Where the progress of a run is published; none when nobody listens.</param>
    /// <param name="logger">Where the run is reported.</param>
    public AutofocusAction(
        DeviceRegistry registry,
        RigId rigId,
        DeviceId cameraId,
        DeviceId focuserId,
        AutofocusOptions options,
        IFocusMetricProvider metrics,
        IEventPublisher? events = null,
        ILogger<AutofocusAction>? logger = null,
        IAcquisitionDefaultsSource? acquisitionDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        options.Validate();

        _registry = registry;
        _metrics = metrics;
        _events = events;
        _logger = logger ?? NullLogger<AutofocusAction>.Instance;
        _acquisitionDefaults = acquisitionDefaults;
        RigId = rigId;
        CameraId = cameraId;
        FocuserId = focuserId;
        Options = options;
    }

    /// <summary>The action for a rig: its camera and its focuser.</summary>
    /// <exception cref="InvalidOperationException">The rig has no focuser.</exception>
    public static AutofocusAction ForRig(
        DeviceRegistry registry, Rig rig, AutofocusOptions options, IFocusMetricProvider metrics, IEventPublisher? events = null,
        ILogger<AutofocusAction>? logger = null, IAcquisitionDefaultsSource? acquisitionDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(rig);

        return rig.FocuserId is { } focuserId
            ? new AutofocusAction(registry, rig.Id, rig.CameraId, focuserId, options, metrics, events, logger, acquisitionDefaults)
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
        using var scope = _logger.Begin((LogContext.RigId, RigId.ToString()));
        var started = Stopwatch.GetTimestamp();
        try
        {
            var focuser = DeviceLookup.ResolveAbsoluteFocuser(_registry, FocuserId);
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

            _logger.LogInformation(
                "Autofocus started for rig {RigId}: camera {CameraId}, focuser {FocuserId} at position {InitialPosition}, " +
                "{SampleCount} samples {StepSize} steps apart, {ExposureSeconds} s exposures",
                RigId, CameraId, FocuserId, focuser.Position, Options.SampleCount, Options.StepSize,
                Options.ExposureDuration.TotalSeconds);

            var measurer = new FocusMeasurementOperation(_registry, RigId, CameraId, FocuserId, _metrics, _acquisitionDefaults);
            var result = await AutofocusEngine.RunAsync(focuser, measurer, Options, Report, cancellationToken);

            _logger.LogInformation(
                "Autofocus completed for rig {RigId}: best position {BestPosition} (from {InitialPosition}), HFR {BestHfr:0.00} px, " +
                "{SampleCount} samples in {Passes} passes, {DurationSeconds:0.0} s",
                RigId, result.BestPosition, result.InitialPosition, result.BestHfr, result.Measurements.Count, result.Attempts,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
            return new SequenceStepResult(result);
        }
        catch (OperationCanceledException)
        {
            // Cancelled or failed: whatever listens is told the run is over, even though the token is cancelled.
            _logger.LogInformation(
                "Autofocus cancelled for rig {RigId} after {DurationSeconds:0.0} s", RigId, Stopwatch.GetElapsedTime(started).TotalSeconds);
            await Publish(new AutofocusProgress(AutofocusPhase.Stopped, 0, 0, 0), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Autofocus failed for rig {RigId} after {DurationSeconds:0.0} s", RigId, Stopwatch.GetElapsedTime(started).TotalSeconds);
            await Publish(new AutofocusProgress(AutofocusPhase.Stopped, 0, 0, 0), CancellationToken.None);
            throw;
        }
    }

    // Every progress report goes to the listeners; each sample taken is also written to the log (Debug).
    private Task Report(AutofocusProgress progress, CancellationToken cancellationToken)
    {
        if (progress is { Phase: AutofocusPhase.Measuring, SampleIndex: > 0, Position: { } position, Hfr: { } hfr })
        {
            _logger.LogDebug(
                "Autofocus sample {SampleIndex} of {SampleCount} (pass {Pass}): position {Position}, HFR {Hfr:0.00} px",
                progress.SampleIndex, progress.SampleCount, progress.Attempt, position, hfr);
        }

        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new AutofocusProgressChanged(RigId, progress), cancellationToken);
    }

    private Task Publish(AutofocusProgress progress, CancellationToken cancellationToken) =>
        _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new AutofocusProgressChanged(RigId, progress), cancellationToken);
}
