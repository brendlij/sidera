using System.Diagnostics;
using Astra.Core.Devices;
using Astra.Core.Events;

namespace Astra.Runtime.Devices;

public sealed partial class SimulatedCamera : ICameraControl
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private TimeSpan _exposureElapsed;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private CameraExposureState _exposureState = CameraExposureState.Idle;
    private TimeSpan? _exposureDuration;

    private readonly IEventPublisher? _events;
    private readonly SyntheticFrameGenerator _frameGenerator;
    private int _exposureCount;

    /// <param name="seed">
    /// Seed for the simulated sky. With a seed the sequence of frames is reproducible;
    /// without one every camera instance produces a different sky.
    /// </param>
    public SimulatedCamera(
        DeviceId id,
        string name = "Simulated Camera",
        IEventPublisher? events = null,
        int? seed = null
    )
    {
        Id = id;
        Name = name;
        _events = events;
        _frameGenerator = new SyntheticFrameGenerator(seed is { } s ? new Random(s) : new Random());
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Camera;

    /// <summary>
    /// A fixed star field this camera sees, when there is one. With a <see cref="PsfSigmaSource"/> that gives a width,
    /// the frames are this sky with its stars that wide; without either they are the random sky of old.
    /// </summary>
    public SimulatedSky? Sky { get; set; }

    /// <summary>
    /// The width (Gaussian sigma, in pixels) the stars have right now, which is what focus does to them; <c>null</c>
    /// when there is nothing that decides it. Read when an exposure ends.
    /// </summary>
    public Func<double?>? PsfSigmaSource { get; set; }

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public CameraExposureState ExposureState
    {
        get { lock (_gate) { return _exposureState; } }
    }

    public TimeSpan? ExposureDuration
    {
        get { lock (_gate) { return _exposureDuration; } }
    }

    public TimeSpan ExposureElapsed
    {
        get { lock (_gate) { return _exposureElapsed; } }
    }

    public double ExposureProgress
    {
        get
        {
            lock (_gate)
            {
                return _exposureDuration is { } duration && duration > TimeSpan.Zero
                    ? Math.Clamp(_exposureElapsed / duration, 0.0, 1.0)
                    : 0.0;
            }
        }
    }

    public event EventHandler? ExposureProgressChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTransition(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting))
        {
            return;
        }

        try
        {
            await PublishConnectionStateChangedAsync(
                DeviceConnectionState.Disconnected,
                DeviceConnectionState.Connecting,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            await SetConnectionStateAsync(DeviceConnectionState.Connected, cancellationToken);
        }
        catch
        {
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool disconnecting;
        lock (_gate)
        {
            if (_connectionState == DeviceConnectionState.Connected
                && _exposureState == CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("Cannot disconnect while an exposure is running.");
            }

            disconnecting = _connectionState == DeviceConnectionState.Connected;
            if (disconnecting)
            {
                _connectionState = DeviceConnectionState.Disconnecting;
            }
        }

        if (!disconnecting)
        {
            return;
        }

        try
        {
            await PublishConnectionStateChangedAsync(
                DeviceConnectionState.Connected,
                DeviceConnectionState.Disconnecting,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        finally
        {
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
        }
    }

    public Task<CameraFrame> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        ExposeCoreAsync(duration, FrameType.Light, null, cancellationToken);

    /// <summary>Applies the settings of the request and exposes; when a setting is refused no exposure is started.</summary>
    public Task<CameraFrame> ExposeAsync(CameraExposureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExposeCoreAsync(request.Duration, request.FrameType, request.Change, cancellationToken);
    }

    private CameraExposureOutcome _outcome = CameraExposureOutcome.None;
    private CancellationTokenSource? _exposureCts;
    private Stopwatch? _exposureClock;
    private bool _stopRequested;
    private bool _abortRequested;
    private TimeSpan? _stoppedAfter;

    public CameraExposureOutcome LastOutcome
    {
        get { lock (_gate) { return _outcome; } }
    }

    /// <summary>Ends the exposure now: the frame of what has been collected is returned, marked as stopped.</summary>
    public Task StopExposureAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_exposureState != CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("No exposure is running.");
            }

            if (_abortRequested)
            {
                throw new InvalidOperationException("The exposure is being aborted.");
            }

            (_stopRequested, _stoppedAfter, cts) = (true, _exposureClock?.Elapsed, _exposureCts);
        }

        cts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Throws the exposure away: the pending exposure ends with an <see cref="OperationCanceledException"/>.</summary>
    public Task AbortExposureAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_exposureState != CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("No exposure is running.");
            }

            (_abortRequested, cts) = (true, _exposureCts);
        }

        cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task<CameraFrame> ExposeCoreAsync(
        TimeSpan duration, FrameType frameType, CameraSettings? change, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Camera is not connected.");
            }
        }

        if (frameType is FrameType.Dark or FrameType.Bias && Capabilities.Value is { HasShutter: false })
        {
            throw new InvalidOperationException($"{Name} has no shutter, so it cannot take a {frameType.ToString().ToLowerInvariant()} frame.");
        }

        if (change is { IsEmpty: false })
        {
            await ApplyAsync(change, cancellationToken);
        }

        using var exposureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Camera is not connected.");
            }

            if (_exposureState == CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("An exposure is already running.");
            }

            _exposureState = CameraExposureState.Exposing;
            _exposureDuration = duration;
            _exposureElapsed = TimeSpan.Zero;
            (_exposureCts, _exposureClock, _stopRequested, _abortRequested, _stoppedAfter) = (exposureCts, stopwatch, false, false, null);
        }

        var completed = false;
        var outcome = CameraExposureOutcome.Failed;

        try
        {
            await PublishExposureStateChangedAsync(
                CameraExposureState.Idle,
                CameraExposureState.Exposing,
                cancellationToken);
            RaiseExposureProgressChanged();

            var stopped = false;
            while (stopwatch.Elapsed < duration)
            {
                var remaining = duration - stopwatch.Elapsed;
                try
                {
                    await Task.Delay(
                        remaining < ProgressInterval ? remaining : ProgressInterval,
                        exposureCts.Token);
                }
                catch (OperationCanceledException) when (StopOnly(cancellationToken))
                {
                    stopped = true;
                    break;
                }

                if (stopwatch.Elapsed < duration)
                {
                    SetElapsed(stopwatch.Elapsed);
                }
            }

            // A stopped exposure is the frame of what was collected so far; its frame still says how long was asked for.
            var taken = stopped ? stopwatch.Elapsed : duration;
            SetElapsed(taken);
            var frame = frameType switch
            {
                FrameType.Dark or FrameType.Bias => _frameGenerator.GenerateDark(taken),
                FrameType.Flat => _frameGenerator.GenerateFlat(taken),
                _ => Sky is { } sky && PsfSigmaSource?.Invoke() is { } sigma
                    ? sky.Render(sigma, taken, Interlocked.Increment(ref _exposureCount))
                    : _frameGenerator.Generate(taken),
            };
            frame = FinishFrame(frame, frameType);
            if (stopped)
            {
                TimeSpan after;
                lock (_gate)
                {
                    after = _stoppedAfter ?? taken;
                }

                var pixels = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
                    ? segment.Array
                    : frame.Pixels.ToArray();
                frame = new CameraFrame(frame.Width, frame.Height, pixels, duration)
                {
                    Acquisition = (frame.Acquisition ?? new FrameAcquisition { FrameType = frameType }) with { Stopped = true, StoppedAfter = after },
                };
            }

            completed = true;
            outcome = stopped ? CameraExposureOutcome.Stopped : CameraExposureOutcome.Completed;
            return frame;
        }
        catch (OperationCanceledException)
        {
            outcome = CameraExposureOutcome.Aborted;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _outcome = outcome;
            }

            if (!completed)
            {
                // Cancelled: keep the portion that actually elapsed instead of jumping to 100%.
                SetElapsed(stopwatch.Elapsed < duration ? stopwatch.Elapsed : duration);
            }

            lock (_gate)
            {
                _exposureState = CameraExposureState.Idle;
            }

            await PublishExposureStateChangedAsync(
                CameraExposureState.Exposing,
                CameraExposureState.Idle,
                CancellationToken.None);
        }
    }

    // The exposure was stopped (and not aborted or cancelled): the wait was only interrupted to deliver what there is.
    private bool StopOnly(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _stopRequested && !_abortRequested && !cancellationToken.IsCancellationRequested;
        }
    }

    private void SetElapsed(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _exposureElapsed = elapsed;
        }

        RaiseExposureProgressChanged();
    }

    private void RaiseExposureProgressChanged()
    {
        // Progress observers must not be able to break an exposure.
        foreach (var handler in ExposureProgressChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

    private Task PublishExposureStateChangedAsync(
        CameraExposureState previous,
        CameraExposureState current,
        CancellationToken cancellationToken
    )
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(
                new CameraExposureStateChanged(Id, previous, current),
                cancellationToken
            );
    }

    private bool TryTransition(DeviceConnectionState from, DeviceConnectionState to)
    {
        lock (_gate)
        {
            if (_connectionState != from)
            {
                return false;
            }

            _connectionState = to;
            return true;
        }
    }

    private async Task SetConnectionStateAsync(
        DeviceConnectionState state,
        CancellationToken cancellationToken
    )
    {
        DeviceConnectionState previous;
        lock (_gate)
        {
            previous = _connectionState;
            _connectionState = state;
        }

        OnConnectionStateSet(state);
        await PublishConnectionStateChangedAsync(previous, state, cancellationToken);
    }

    private Task PublishConnectionStateChangedAsync(
        DeviceConnectionState previous,
        DeviceConnectionState current,
        CancellationToken cancellationToken
    )
    {
        return _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(
                new DeviceConnectionStateChanged(Id, previous, current),
                cancellationToken
            );
    }
}
