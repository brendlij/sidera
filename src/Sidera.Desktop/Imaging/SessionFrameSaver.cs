using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Imaging;
using Sidera.Core.Location;
using Sidera.Desktop.Settings;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Imaging;

/// <summary>
/// Saves the frames of a session as FITS files, as the settings say (Settings → Imaging): where, in which folders and under which name. What is only known while the exposure starts (the filter in the
/// light path, where the mount points, the temperature of the sensor) is read then; the file is written afterwards on a thread of its own, so that writing never holds up the next exposure. A file is
/// written under a temporary name and renamed when it is complete, so that no half file is ever taken for a frame. A frame that cannot be saved is told to the user, once for each kind of problem.
/// </summary>
public sealed class SessionFrameSaver : IFrameSink, IAsyncDisposable
{
    private const int PendingWarning = 6;

    private readonly SideraRuntimeHost _host;
    private readonly Func<ImagingSettings> _settings;
    private readonly Func<ObservingSite?> _site;
    private readonly Action<string, string>? _problem;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _now;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _told = new(StringComparer.Ordinal);
    private readonly Task _writer;
    private int _pending;

    /// <param name="problem">Says a problem to the user: a title and a sentence.</param>
    public SessionFrameSaver(
        SideraRuntimeHost host, Func<ImagingSettings> settings, Func<ObservingSite?> site, Action<string, string>? problem = null, ILogger? logger = null, Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        _host = host;
        _settings = settings;
        _site = site;
        _problem = problem;
        _logger = logger ?? NullLogger.Instance;
        _now = now ?? (() => DateTime.Now);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>A frame was written: the path of its file. Raised on the thread of the writer.</summary>
    public event EventHandler<string>? Saved;

    /// <summary>The file that was written last; <c>null</c> before the first.</summary>
    public string? LastSavedPath { get; private set; }

    /// <summary>How many frames are waiting to be written.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public IFrameCapture Begin(DeviceId cameraId, FrameLabel? label)
    {
        var settings = _settings();
        if (!settings.SaveSessionFrames)
        {
            return NoCapture.Instance;
        }

        var startedLocal = _now();
        var startedAt = new DateTimeOffset(startedLocal);
        var context = FrameExporter.CaptureContext(_host, cameraId, startedAt);
        var rig = _host.RigRegistry.GetAll().FirstOrDefault(r => r.CameraId == cameraId);
        _host.DeviceRegistry.TryGet(cameraId, out var camera);
        var filter = FilterOf(rig?.FilterWheelId);
        double? temperature = null;
        try
        {
            temperature = (camera as ICameraControl)?.Telemetry?.CcdTemperature;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A camera that does not say its temperature: it is not written.
        }

        return new Capture(this, settings, cameraId, label, startedLocal, context with
        {
            ObjectName = label?.TargetName,
            FilterName = filter,
            SensorTemperatureCelsius = temperature is { } t && double.IsFinite(t) ? t : null,
        }, camera?.Name, rig?.Name);
    }

    /// <summary>Completes when every frame handed in before this call has been written (or has failed).</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Job(null, null, null, null, done)))
        {
            done.SetResult();
        }

        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private string? FilterOf(DeviceId? wheelId)
    {
        if (wheelId is null || !_host.DeviceRegistry.TryGet(wheelId.Value, out var device) || device is not IFilterWheel { ConnectionState: DeviceConnectionState.Connected } wheel)
        {
            return null;
        }

        try
        {
            return wheel.CurrentSlot.Name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void Enqueue(Job job)
    {
        if (Interlocked.Increment(ref _pending) > PendingWarning)
        {
            Tell("frames-queue", "Frames are saved slowly", "The disk takes more time to write the frames than the camera takes to deliver them. Check the save folder and the drive.");
        }

        if (!_queue.Writer.TryWrite(job))
        {
            Interlocked.Decrement(ref _pending);
            Tell("frames-closed", "A frame was not saved", "Sidera is closing.");
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (job.Flush is { } flush)
            {
                flush.SetResult();
                continue;
            }

            try
            {
                var path = Write(job);
                LastSavedPath = path;
                _logger.LogInformation("Frame saved: {Path}", path);
                Saved?.Invoke(this, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogError(ex, "A frame could not be saved");
                Tell("frames-write", "A frame was not saved", $"{ex.Message} The frame is lost. Check the save folder in Settings → Imaging and the free space of the drive.");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private string Write(Job job)
    {
        var settings = job.Settings!;
        var values = job.Values!;
        var root = Path.GetFullPath(settings.FramesRoot);
        var folder = Path.GetFullPath(Path.Combine([root, .. FrameNaming.Folders(settings.FolderPattern, values)]));
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The folder pattern leads out of the save folder.");
        }

        Directory.CreateDirectory(folder);
        var key = folder + "|" + settings.FilePattern;
        var number = _counters.GetValueOrDefault(key, 0);
        string path;
        do
        {
            number++;
            path = Path.Combine(folder, FrameNaming.FileName(settings.FilePattern, values, number) + ".fits");
        }
        while (File.Exists(path));

        _counters[key] = number;
        var temporary = path + ".part";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                FitsImageWriter.Write(stream, job.Frame!, job.Metadata);
            }

            File.Move(temporary, path);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // The half file stays where it is: it ends in .part, so it is not taken for a frame.
            }

            throw;
        }

        return path;
    }

    private void Tell(string key, string title, string message)
    {
        lock (_told)
        {
            if (!_told.Add(key))
            {
                return;
            }
        }

        _problem?.Invoke(title, message);
    }

    private sealed record Job(CameraFrame? Frame, FitsMetadata? Metadata, FrameNameValues? Values, ImagingSettings? Settings, TaskCompletionSource? Flush = null);

    private sealed class NoCapture : IFrameCapture
    {
        public static readonly NoCapture Instance = new();

        public void Complete(CameraFrame frame)
        {
        }

        public void Abandon()
        {
        }
    }

    private sealed class Capture(
        SessionFrameSaver owner, ImagingSettings settings, DeviceId cameraId, FrameLabel? label, DateTime startedLocal, FitsMetadata context, string? cameraName, string? setupName) : IFrameCapture
    {
        public void Complete(CameraFrame frame)
        {
            var metadata = FrameExporter.MetadataFor(frame, owner._host, cameraId, owner._site(), context) with { ExposureSeconds = frame.ExposureDuration.TotalSeconds };
            var acquisition = frame.Acquisition;
            var values = new FrameNameValues(
                label?.TargetName, setupName, cameraName ?? cameraId.Value, context.FilterName, acquisition?.FrameType ?? FrameType.Light, frame.ExposureDuration.TotalSeconds, startedLocal,
                acquisition?.Gain, acquisition?.Offset, context.SensorTemperatureCelsius, acquisition?.BinX ?? 1, acquisition?.BinY ?? 1);
            owner.Enqueue(new Job(frame, metadata, values, settings));
        }

        public void Abandon()
        {
        }
    }
}
