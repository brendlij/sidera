using System.Collections.Concurrent;
using System.Globalization;
using Sidera.Core.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.Devices;

/// <summary>The acquisition settings of an exposure cannot be used with the camera that was to take it. Nothing was started.</summary>
public sealed class AcquisitionException(string message, IReadOnlyList<string> problems) : InvalidOperationException(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The acquisition defaults of the cameras, as the application keeps them (in the equipment configuration) and the runtime reads
/// them: what each camera normally takes frames with. Thread-safe; the application updates it, the exposures read it.
/// </summary>
public sealed class AcquisitionDefaultsRegistry : IAcquisitionDefaultsSource
{
    private readonly ConcurrentDictionary<DeviceId, AcquisitionIntent> _defaults = new();

    public AcquisitionIntent? DefaultsFor(DeviceId cameraId) => _defaults.TryGetValue(cameraId, out var intent) ? intent : null;

    /// <summary>Sets the defaults of a camera; <c>null</c> (or nothing set) removes them.</summary>
    public void Set(DeviceId cameraId, AcquisitionIntent? defaults)
    {
        if (defaults is null || defaults.IsDefault)
        {
            _defaults.TryRemove(cameraId, out _);
        }
        else
        {
            _defaults[cameraId] = defaults;
        }
    }
}

/// <summary>
/// The one way an exposure with acquisition settings is taken, for a sequence step, a manual exposure and autofocus alike:
/// the settings are resolved against the camera (what the exposure says, else the default of the camera, else the camera as it
/// is), checked against what the connected camera reports it supports, and handed to the camera together with the exposure so
/// that applying them and starting the exposure are one operation. It knows capabilities and nothing of any backend. The caller
/// holds the camera's resource; nothing else can change the camera between applying and exposing.
/// </summary>
public static class AcquisitionExposer
{
    /// <summary>
    /// What an autofocus exposure asks for: the whole sensor, unbinned, a light frame; gain, offset and readout come from the
    /// defaults of the camera. A narrow region or a 2x2 binning that an imaging exposure left on the camera is never used to focus.
    /// </summary>
    public static AcquisitionIntent Autofocus { get; } = new()
    {
        BinX = 1,
        BinY = 1,
        Region = AcquisitionRegion.Full,
    };

    /// <summary>
    /// An exposure to measure focus with: with <see cref="Autofocus"/> for a camera that can apply acquisition settings; a camera that
    /// cannot say what it supports exposes with the settings it has, as every camera did before.
    /// </summary>
    public static Task<CameraFrame> ExposeForFocusAsync(
        ICamera camera, TimeSpan duration, IAcquisitionDefaultsSource? defaults, ILogger? logger, CancellationToken cancellationToken) =>
        camera is ICameraControl
            ? ExposeAsync(camera, duration, Autofocus, defaults, logger, cancellationToken)
            : camera.ExposeAsync(duration, cancellationToken);

    /// <exception cref="AcquisitionException">The camera does not support what the exposure asks for; no exposure was started.</exception>
    public static async Task<CameraFrame> ExposeAsync(
        ICamera camera,
        TimeSpan duration,
        AcquisitionIntent? intent,
        IAcquisitionDefaultsSource? defaults,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(camera);
        intent ??= AcquisitionIntent.Default;
        logger ??= NullLogger.Instance;

        if (camera is not ICameraControl control)
        {
            // A camera that cannot say what it supports cannot be asked for more than an exposure.
            if (intent.HasOverrides || intent.FrameType != FrameType.Light)
            {
                throw new AcquisitionException(
                    $"{camera.Name} cannot apply acquisition settings, so it can only take light frames with its own settings.",
                    ["The camera does not report capabilities."]);
            }

            return await camera.ExposeAsync(duration, cancellationToken);
        }

        var plan = AcquisitionResolver.Resolve(
            intent, defaults?.DefaultsFor(camera.Id), duration, control.Capabilities, control.Settings);

        if (plan.Status == AcquisitionStatus.Invalid)
        {
            logger.LogWarning(
                "Camera {CameraId} rejects the acquisition settings of the exposure: {Problems}", camera.Id, string.Join(" ", plan.Problems));
            throw new AcquisitionException($"{camera.Name}: {string.Join(" ", plan.Problems)}", plan.Problems);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var e = plan.Effective;
            logger.LogDebug(
                "Exposure on {CameraId}: {ExposureSeconds} s, Gain {Gain}, Offset {Offset}, BinX {BinX}, BinY {BinY}, Region {Region}, ReadoutMode {ReadoutMode}, FrameType {FrameType}; changing {Changed}",
                camera.Id,
                duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                e.GainName ?? e.Gain?.ToString(CultureInfo.InvariantCulture),
                e.OffsetName ?? e.Offset?.ToString(CultureInfo.InvariantCulture),
                e.BinX, e.BinY,
                e.Width is { } w && e.Height is { } h ? $"{w}x{h}@{e.StartX},{e.StartY}" : null,
                e.ReadoutMode, plan.FrameType,
                plan.Change.IsEmpty ? "nothing" : "settings");
        }

        try
        {
            return await control.ExposeAsync(new CameraExposureRequest(duration, plan.FrameType, plan.Change), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Exposure of {CameraId} could not be taken", camera.Id);
            throw;
        }
    }
}
