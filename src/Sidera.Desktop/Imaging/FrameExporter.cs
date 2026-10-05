using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime;

namespace Sidera.Desktop.Imaging;

/// <summary>
/// Saving the frame that is shown. A FITS file is the data: the frame as it was taken, never stretched, with what is known about how it was taken and nothing that is not (no value is
/// invented; a value that is not known is left out). A PNG is a picture of the display, stretched or linear as it is shown, and says so in its name.
/// </summary>
public static class FrameExporter
{
    /// <summary>The metadata of a frame for its FITS header, from what is known now (the rig of the camera, its optics, the site) and from what was recorded when it was taken.</summary>
    /// <param name="capture">What was known at the time of the exposure and cannot be asked for later (where the mount pointed, when the exposure started).</param>
    public static FitsMetadata MetadataFor(CameraFrame frame, SideraRuntimeHost? host, DeviceId? cameraId, ObservingSite? site, FitsMetadata? capture = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var metadata = capture ?? new FitsMetadata();
        IDevice? camera = null;
        Rig? rig = null;
        if (host is not null && cameraId is { } id)
        {
            host.DeviceRegistry.TryGet(id, out camera);
            rig = host.RigRegistry.GetAll().FirstOrDefault(r => r.CameraId == id);
        }

        var binX = frame.Acquisition?.BinX ?? 1;
        var binY = frame.Acquisition?.BinY ?? 1;
        var geometry = rig is null ? null : OpticalTrainGeometry.Resolve(rig.Optics, SensorGeometry.For(camera));
        return metadata with
        {
            Instrument = metadata.Instrument ?? camera?.Name,
            Telescope = metadata.Telescope ?? rig?.Name,
            FocalLengthMm = metadata.FocalLengthMm ?? geometry?.FocalLengthMm,
            PixelSizeXMicrons = metadata.PixelSizeXMicrons ?? (geometry?.PixelSizeXMicrons is { } px ? px * binX : null),
            PixelSizeYMicrons = metadata.PixelSizeYMicrons ?? (geometry?.PixelSizeYMicrons is { } py ? py * binY : null),
            SiteLatitudeDegrees = metadata.SiteLatitudeDegrees ?? site?.LatitudeDegrees,
            SiteLongitudeDegrees = metadata.SiteLongitudeDegrees ?? site?.LongitudeDegrees,
            SiteElevationMeters = metadata.SiteElevationMeters ?? site?.ElevationMeters,
        };
    }

    /// <summary>
    /// What is known at the moment a frame was taken: where the mount of the rig points (only when it is connected and says so) and the time. The pointing is approximate (the mount's, not a
    /// plate solution), and for a frame whose mount is not known it is not there.
    /// </summary>
    public static FitsMetadata CaptureContext(SideraRuntimeHost host, DeviceId cameraId, DateTimeOffset startedAt)
    {
        var rig = host.RigRegistry.GetAll().FirstOrDefault(r => r.CameraId == cameraId);
        double? ra = null, dec = null;
        if (rig?.MountId is { } mountId && host.DeviceRegistry.TryGet(mountId, out var device) && device is IMount { ConnectionState: DeviceConnectionState.Connected } mount)
        {
            try
            {
                var at = mount.Coordinates;
                ra = at.RightAscensionHours * 15;
                dec = at.DeclinationDegrees;
            }
            catch (Exception)
            {
                // A mount that does not answer: the pointing is not known, so it is not written.
            }
        }

        return new FitsMetadata { ObservedAt = startedAt, RightAscensionDegrees = ra, DeclinationDegrees = dec };
    }

    public static void SaveFits(CameraFrame frame, string path, FitsMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.Create(path);
        FitsImageWriter.Write(stream, frame, metadata);
    }

    /// <summary>A picture of the display: the same stretch as on the screen.</summary>
    public static void SavePng(CameraFrame frame, string path, bool autoStretch)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.Create(path);
        PngImageWriter.Write(stream, ImageStretch.ToGray8(frame, autoStretch), frame.Width, frame.Height);
    }

    /// <summary>A name for a file: "Sidera_light_20261005_201530_300s" and the extension; a PNG says that it is a picture of the display.</summary>
    public static string SuggestName(CameraFrame frame, string extension, DateTime? when = null, bool? autoStretch = null)
    {
        var type = (frame.Acquisition?.FrameType ?? FrameType.Light).ToString().ToLowerInvariant();
        var seconds = frame.ExposureDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var picture = autoStretch is { } stretched ? (stretched ? "_display_stretched" : "_display_linear") : string.Empty;
        return $"Sidera_{type}_{(when ?? DateTime.Now):yyyyMMdd_HHmmss}_{seconds}s{picture}.{extension.TrimStart('.')}";
    }
}
