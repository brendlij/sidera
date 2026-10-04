using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Imaging;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Focusing;

/// <summary>
/// Measures the focus of one rig where its focuser stands now: reads the focuser position, exposes with the camera of
/// the rig, and has the <see cref="IFocusMetricProvider"/> judge the frame. It is a real exposure with the real camera
/// even in a simulation, so that a metric that analyses pixels can replace the simulated one without changing
/// anything else.
/// <para>
/// It takes no resources. The caller owns the camera and the focuser for as long as it measures (the autofocus action
/// holds both for the whole run), so that nothing else can expose or move in between.
/// </para>
/// </summary>
public sealed class FocusMeasurementOperation : IFocusMeasurer
{
    private readonly DeviceRegistry _registry;
    private readonly RigId _rigId;
    private readonly DeviceId _cameraId;
    private readonly DeviceId _focuserId;
    private readonly IFocusMetricProvider _metrics;
    private readonly IAcquisitionDefaultsSource? _acquisitionDefaults;

    public FocusMeasurementOperation(
        DeviceRegistry registry,
        RigId rigId,
        DeviceId cameraId,
        DeviceId focuserId,
        IFocusMetricProvider metrics,
        IAcquisitionDefaultsSource? acquisitionDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(metrics);

        _registry = registry;
        _rigId = rigId;
        _cameraId = cameraId;
        _focuserId = focuserId;
        _metrics = metrics;
        _acquisitionDefaults = acquisitionDefaults;
    }

    /// <exception cref="InvalidOperationException">
    /// A device is unknown, of the wrong kind or not connected; the focuser moved during the exposure; or the metric
    /// could not measure the frame, or reported something that is not a measurement at the exposure position.
    /// </exception>
    public async Task<FocusMeasurement> MeasureAsync(TimeSpan exposureDuration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exposureDuration, TimeSpan.Zero);

        var focuser = DeviceLookup.ResolveAbsoluteFocuser(_registry, _focuserId);
        var camera = DeviceLookup.Resolve<ICamera>(_registry, _cameraId, "camera");

        if (focuser.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Focuser '{_focuserId}' is not connected.");
        }

        if (camera.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Camera '{_cameraId}' is not connected.");
        }

        var position = focuser.Position;
        // Autofocus has acquisition requirements of its own (the whole sensor, unbinned): what an imaging exposure left on the camera
        // is not used to focus.
        var frame = await AcquisitionExposer.ExposeForFocusAsync(camera, exposureDuration, _acquisitionDefaults, null, cancellationToken);

        if (focuser.Position != position || focuser.MotionState != FocuserMotionState.Idle)
        {
            throw new InvalidOperationException("The focuser moved during the focus exposure.");
        }

        FocusMeasurement measurement;
        try
        {
            measurement = await _metrics.MeasureAsync(
                new FocusMetricInput(_rigId, _cameraId, _focuserId, position, frame), cancellationToken);
        }
        catch (FrameAnalysisException ex)
        {
            // The frame could not be measured (no stars, too few): for the user that is why the autofocus failed.
            throw new AutofocusFailedException($"Autofocus failed: {ex.Reason}.");
        }
        catch (ArgumentException ex)
        {
            // FocusMeasurement refuses an HFR that is not a finite, positive number.
            throw new InvalidOperationException("The focus metric returned an invalid HFR.", ex);
        }

        if (measurement is null)
        {
            throw new InvalidOperationException("The focus metric returned no measurement.");
        }

        return measurement.FocuserPosition == position
            ? measurement
            : throw new InvalidOperationException("The focus metric measured another focuser position than the frame was taken at.");
    }
}
