using System.Diagnostics;
using Astra.Ascom.Drivers;
using Astra.Ascom.Infrastructure;
using Astra.Core.Devices;
using Astra.Core.Events;
using Microsoft.Extensions.Logging;

namespace Astra.Ascom.Cameras;

/// <summary>
/// An ASCOM camera as Astra's <see cref="ICamera"/>: single-plane light frames, as they come out of the camera.
/// <para>
/// An exposure is <c>StartExposure(seconds, true)</c> (true: a light frame; false would be a dark), polling of
/// <c>ImageReady</c>, then one read of <c>ImageArray</c>, which <see cref="AscomImageConverter"/> turns into a
/// <see cref="CameraFrame"/>. Nothing about the camera is changed: binning, subframe, gain, cooling and readout mode are
/// left as the driver has them, and the frame has the size the driver announces. The values are the camera's ADUs; a
/// camera with a lower MaxADU than 65535 is not scaled.
/// </para>
/// <para>
/// An exposure is never retried. Cancelling (or a timeout) calls <c>AbortExposure</c> when the camera can abort; a camera
/// that cannot is never claimed to have stopped, and its exposure may still be running when the next one is started.
/// </para>
/// </summary>
public sealed class AscomCamera : AscomDevice<IAscomCameraDriver>, ICameraControl
{
    private readonly CapabilityHolder<CameraCapabilities> _capabilities = new();
    private CameraSettings? _settings;
    private CameraTelemetry? _telemetry;
    private bool _exposed;
    private readonly IAscomDriverFactory _drivers;
    private readonly object _state = new();
    private CameraExposureState _exposureState = CameraExposureState.Idle;
    private TimeSpan? _exposureDuration;
    private TimeSpan _exposureElapsed;
    private bool _canAbort;

    public AscomCamera(
        DeviceId id,
        string name,
        string progId,
        IAscomDriverFactory drivers,
        IEventPublisher? events = null,
        ILogger? logger = null,
        AscomTimings? timings = null)
        : base(id, name, DeviceType.Camera, progId, events, logger, timings)
    {
        _drivers = drivers;
    }

    public CameraExposureState ExposureState
    {
        get { lock (_state) { return _exposureState; } }
    }

    public TimeSpan? ExposureDuration
    {
        get { lock (_state) { return _exposureDuration; } }
    }

    public TimeSpan ExposureElapsed
    {
        get { lock (_state) { return _exposureElapsed; } }
    }

    public double ExposureProgress
    {
        get
        {
            lock (_state)
            {
                return _exposureDuration is { } duration && duration > TimeSpan.Zero
                    ? Math.Clamp(_exposureElapsed / duration, 0.0, 1.0)
                    : 0.0;
            }
        }
    }

    public event EventHandler? ExposureProgressChanged;

    protected override bool IsBusy => ExposureState == CameraExposureState.Exposing;

    protected override string BusyDescription => "an exposure is running";

    protected override IAscomCameraDriver CreateDriver() => _drivers.CreateCamera(ProgId);

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged
    {
        add => _capabilities.Changed += value;
        remove => _capabilities.Changed -= value;
    }

    public DeviceCapabilities<CameraCapabilities> Capabilities => _capabilities.Current;

    public CameraSettings? Settings
    {
        get { lock (_state) { return _settings; } }
    }

    public CameraTelemetry? Telemetry
    {
        get { lock (_state) { return _telemetry; } }
    }

    protected override void OnConnected(IAscomCameraDriver driver)
    {
        var (x, y, numX, numY, maxAdu, canAbort) =
            (driver.CameraXSize, driver.CameraYSize, driver.NumX, driver.NumY, driver.MaxAdu, driver.CanAbortExposure);
        if (x <= 0 || y <= 0)
        {
            throw new AscomUnsupportedException(
                Id, ProgId, "connect", $"{Name} ({ProgId}) reports a sensor of {x} x {y} pixels; it cannot be used.");
        }

        var capabilities = ProbeCapabilities(driver, x, y, maxAdu, canAbort);
        var settings = ReadSettings(driver, capabilities);
        var telemetry = ReadTelemetry(driver, capabilities, includeLastExposure: false);
        lock (_state)
        {
            _canAbort = canAbort;
            _exposureState = CameraExposureState.Idle;
            _settings = settings;
            _telemetry = telemetry;
        }

        _capabilities.Set(capabilities, this);
        foreach (var note in capabilities.Notes)
        {
            Logger.LogDebug("{Device}: capability probe: {Note}", Name, note);
        }

        Logger.LogInformation(
            "{Device}: sensor {X} x {Y}, image {NumX} x {NumY}, MaxADU {MaxAdu}, can abort {CanAbort}, gain {Gain}, offset {Offset}, binning up to {MaxBin}, cooling {Cooling}",
            Name, x, y, numX, numY, maxAdu, canAbort, capabilities.Gain is not null, capabilities.Offset is not null,
            capabilities.MaxBinX, capabilities.SupportsCooling);
        if (maxAdu is > 0 and < ushort.MaxValue)
        {
            Logger.LogWarning(
                "{Device} has a MaxADU of {MaxAdu}: frames keep the camera's own values and are not scaled to 16 bit", Name, maxAdu);
        }
    }

    // Reads only. The standard flags are read as they are; members that exist only on some drivers (gain, cooler, Bayer
    // offsets ...) are tried, and one that is not implemented is a capability the camera does not have.
    private CameraCapabilities ProbeCapabilities(IAscomCameraDriver d, int width, int height, int maxAdu, bool canAbort)
    {
        var probe = new CapabilityProbe(Name);
        bool Flag(string member, Func<bool> read) => probe.Read(member, read, false);

        var sensor = probe.Try("SensorType", () => d.SensorType) ?? SensorKind.Unknown;
        (int X, int Y)? bayer = null;
        if (sensor is not (SensorKind.Monochrome or SensorKind.Unknown))
        {
            var bx = probe.Try("BayerOffsetX", () => d.BayerOffsetX);
            var by = probe.Try("BayerOffsetY", () => d.BayerOffsetY);
            if (bx is { } ox && by is { } oy)
            {
                bayer = (ox, oy);
            }
        }

        var sensorName = probe.TryRef("SensorName", () => d.SensorName);
        var pixelX = probe.Try("PixelSizeX", () => d.PixelSizeX);
        var pixelY = probe.Try("PixelSizeY", () => d.PixelSizeY);
        var exposureMin = probe.Try("ExposureMin", () => d.ExposureMin);
        var exposureMax = probe.Try("ExposureMax", () => d.ExposureMax);
        var exposureResolution = probe.Try("ExposureResolution", () => d.ExposureResolution);
        var electrons = probe.Try("ElectronsPerADU", () => d.ElectronsPerAdu);
        var fullWell = probe.Try("FullWellCapacity", () => d.FullWellCapacity);

        var maxBinX = Math.Max(1, probe.Try("MaxBinX", () => d.MaxBinX) ?? 1);
        var maxBinY = Math.Max(1, probe.Try("MaxBinY", () => d.MaxBinY) ?? 1);
        var asymmetric = Flag("CanAsymmetricBin", () => d.CanAsymmetricBin);

        var modes = probe.TryRef("ReadoutModes", () => d.ReadoutModes) ?? [];
        var canFast = Flag("CanFastReadout", () => d.CanFastReadout);
        var canSetTemperature = Flag("CanSetCCDTemperature", () => d.CanSetCcdTemperature);
        var cooler = probe.Try("CoolerOn", () => d.CoolerOn) is not null;

        return new CameraCapabilities
        {
            Driver = probe.Read("identity", () => d.Identity, new DriverMetadata()),
            CanAbortExposure = canAbort,
            CanStopExposure = Flag("CanStopExposure", () => d.CanStopExposure),
            MinExposureSeconds = exposureMin is > 0 ? exposureMin : null,
            MaxExposureSeconds = exposureMax is > 0 ? exposureMax : null,
            ExposureResolutionSeconds = exposureResolution is > 0 ? exposureResolution : null,
            SensorWidth = width,
            SensorHeight = height,
            PixelSizeXMicrons = pixelX is > 0 ? pixelX : null,
            PixelSizeYMicrons = pixelY is > 0 ? pixelY : null,
            MaxAdu = maxAdu,
            ElectronsPerAdu = electrons is > 0 ? electrons : null,
            FullWellCapacity = fullWell is > 0 ? fullWell : null,
            SensorType = sensor,
            SensorName = string.IsNullOrWhiteSpace(sensorName) ? null : sensorName,
            BayerOffset = bayer,
            HasShutter = Flag("HasShutter", () => d.HasShutter),
            MaxBinX = maxBinX,
            MaxBinY = maxBinY,
            CanAsymmetricBin = asymmetric,
            SupportsSubframe = probe.Try("NumX", () => d.NumX) is not null && probe.Try("StartX", () => d.StartX) is not null,
            Gain = ProbeControl(probe, "Gain", () => d.Gain, () => d.GainMin, () => d.GainMax, () => d.Gains),
            Offset = ProbeControl(probe, "Offset", () => d.Offset, () => d.OffsetMin, () => d.OffsetMax, () => d.Offsets),
            ReadoutModes = modes,
            CanFastReadout = canFast,
            CanSetCcdTemperature = canSetTemperature,
            HasCooler = cooler,
            CanGetCoolerPower = Flag("CanGetCoolerPower", () => d.CanGetCoolerPower),
            HasCcdTemperature = probe.Try("CCDTemperature", () => d.CcdTemperature) is { } t && double.IsFinite(t),
            HasHeatSinkTemperature = probe.Try("HeatSinkTemperature", () => d.HeatSinkTemperature) is { } h && double.IsFinite(h),
            Notes = probe.Notes.ToList(),
        };
    }

    // Gain and offset exist as a min/max range or as a list of named values, never both; the driver throws for the form it
    // does not use. A value that cannot be read at all means the camera has no such setting.
    private static IntegerControl? ProbeControl(
        CapabilityProbe probe, string name, Func<int> value, Func<int> min, Func<int> max, Func<IReadOnlyList<string>> list)
    {
        if (probe.Try(name, value) is null)
        {
            return null;
        }

        var lo = probe.Try(name + "Min", min);
        var hi = probe.Try(name + "Max", max);
        if (lo is { } low && hi is { } high && high >= low)
        {
            return IntegerControl.Range(low, high);
        }

        var choices = probe.TryRef(name + "s", list);
        return choices is { Count: > 0 } ? IntegerControl.List(choices) : null;
    }

    private static CameraSettings ReadSettings(IAscomCameraDriver d, CameraCapabilities c)
    {
        T? Opt<T>(bool offered, Func<T> read) where T : struct
        {
            if (!offered)
            {
                return null;
            }

            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        return new CameraSettings
        {
            Gain = Opt(c.Gain is not null, () => d.Gain),
            Offset = Opt(c.Offset is not null, () => d.Offset),
            BinX = Opt(true, () => d.BinX),
            BinY = Opt(true, () => d.BinY),
            StartX = Opt(c.SupportsSubframe, () => d.StartX),
            StartY = Opt(c.SupportsSubframe, () => d.StartY),
            NumX = Opt(c.SupportsSubframe, () => d.NumX),
            NumY = Opt(c.SupportsSubframe, () => d.NumY),
            ReadoutMode = Opt(c.ReadoutModes.Count > 0, () => d.ReadoutMode),
            FastReadout = Opt(c.CanFastReadout, () => d.FastReadout),
            TargetTemperature = Opt(c.CanSetCcdTemperature, () => d.SetCcdTemperature),
            CoolerOn = Opt(c.HasCooler, () => d.CoolerOn),
        };
    }

    private static CameraTelemetry ReadTelemetry(IAscomCameraDriver d, CameraCapabilities c, bool includeLastExposure)
    {
        T? Opt<T>(bool offered, Func<T> read) where T : struct
        {
            if (!offered)
            {
                return null;
            }

            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

        string? start = null;
        double? last = null;
        if (includeLastExposure)
        {
            last = Finite(Opt(true, () => d.LastExposureDuration));
            try
            {
                start = d.LastExposureStartTime;
            }
            catch
            {
                start = null;
            }
        }

        return new CameraTelemetry
        {
            CcdTemperature = Finite(Opt(c.HasCcdTemperature, () => d.CcdTemperature)),
            CoolerPower = Finite(Opt(c.CanGetCoolerPower, () => d.CoolerPower)),
            HeatSinkTemperature = Finite(Opt(c.HasHeatSinkTemperature, () => d.HeatSinkTemperature)),
            CoolerOn = Opt(c.HasCooler, () => d.CoolerOn),
            LastExposureSeconds = last,
            LastExposureStart = start,
        };
    }

    protected override void OnDisconnected()
    {
        lock (_state)
        {
            _exposureState = CameraExposureState.Idle;
            _settings = null;
            _telemetry = null;
        }

        _capabilities.Reset(this);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        bool hadExposure;
        lock (_state)
        {
            hadExposure = _telemetry?.LastExposureSeconds is not null || _exposed;
        }

        var (settings, telemetry) = await CallAsync(
            "read the state of",
            d => (ReadSettings(d, capabilities), ReadTelemetry(d, capabilities, hadExposure)),
            cancellationToken);
        lock (_state)
        {
            _settings = settings;
            _telemetry = telemetry;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ApplyAsync(CameraSettings change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (change.IsEmpty)
        {
            return;
        }

        // Binning, subframe, gain, offset and readout belong to the exposure: not while one runs. Cooling can change.
        var needsIdle = change.Gain is not null || change.Offset is not null || change.BinX is not null || change.BinY is not null
            || change.ChangesSubframe || change.ReadoutMode is not null || change.FastReadout is not null;
        if (needsIdle && ExposureState == CameraExposureState.Exposing)
        {
            throw new InvalidOperationException("The camera is exposing; its settings can be changed when the exposure is over.");
        }

        var current = Settings ?? new CameraSettings();
        var problems = CameraSettingsRules.Problems(capabilities, current, change);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(change));
        }

        using var scope = BeginScope();
        Logger.LogInformation("Applying settings to {Device}: {Change}", Name, change);
        try
        {
            await CallAsync(
                "change the settings of",
                d =>
                {
                    var binChanged = change.BinX is not null || change.BinY is not null;
                    if (binChanged)
                    {
                        // Binning first: drivers reset the subframe when the binning changes.
                        var bx = change.BinX ?? current.BinX ?? d.BinX;
                        var by = change.BinY ?? current.BinY ?? d.BinY;
                        d.BinX = bx;
                        d.BinY = by;
                    }

                    if (change.ChangesSubframe || binChanged)
                    {
                        var binX = d.BinX;
                        var binY = d.BinY;
                        var width = capabilities.SensorWidth / Math.Max(1, binX);
                        var height = capabilities.SensorHeight / Math.Max(1, binY);

                        // A new binning without a subframe means the whole sensor at that binning, never a stale
                        // subframe of the old one.
                        var startX = change.StartX ?? (change.ChangesSubframe ? current.StartX ?? 0 : 0);
                        var startY = change.StartY ?? (change.ChangesSubframe ? current.StartY ?? 0 : 0);
                        var numX = change.NumX ?? (change.ChangesSubframe ? current.NumX ?? width : width);
                        var numY = change.NumY ?? (change.ChangesSubframe ? current.NumY ?? height : height);
                        d.StartX = startX;
                        d.StartY = startY;
                        d.NumX = numX;
                        d.NumY = numY;
                    }

                    if (change.Gain is { } gain)
                    {
                        d.Gain = gain;
                    }

                    if (change.Offset is { } offset)
                    {
                        d.Offset = offset;
                    }

                    if (change.ReadoutMode is { } mode)
                    {
                        d.ReadoutMode = mode;
                    }

                    if (change.FastReadout is { } fast)
                    {
                        d.FastReadout = fast;
                    }

                    if (change.TargetTemperature is { } target)
                    {
                        d.SetCcdTemperature = target;
                    }

                    if (change.CoolerOn is { } on)
                    {
                        d.CoolerOn = on;
                    }
                },
                cancellationToken);
        }
        finally
        {
            // Whatever happened, the settings shown are the ones the camera really has.
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("{Device}: the settings could not be read back: {Reason}", Name, ex.Message);
            }
        }
    }

    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    /// <exception cref="InvalidOperationException">The camera is not connected or is already exposing.</exception>
    public Task<CameraFrame> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        ExposeCoreAsync(duration, FrameType.Light, null, cancellationToken);

    /// <summary>
    /// Applies the settings of the request and exposes, in this order and nothing in between: a setting that cannot be applied
    /// stops the operation before the exposure starts. What was applied before it failed stays applied, and
    /// <see cref="Settings"/> says what the camera has.
    /// </summary>
    public Task<CameraFrame> ExposeAsync(CameraExposureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExposeCoreAsync(request.Duration, request.FrameType, request.Change, cancellationToken);
    }

    private async Task<CameraFrame> ExposeCoreAsync(
        TimeSpan duration, FrameType frameType, CameraSettings? change, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        Session();

        if (frameType is FrameType.Dark or FrameType.Bias && Capabilities.Value is { HasShutter: false })
        {
            throw new AscomUnsupportedException(
                Id, ProgId, "expose", $"{Name} ({ProgId}) has no shutter, so it cannot take a {frameType.ToString().ToLowerInvariant()} frame.");
        }

        if (change is { IsEmpty: false })
        {
            try
            {
                await ApplyAsync(change, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Applying the acquisition settings to {Device} failed; no exposure is started", Name);
                throw;
            }
        }

        lock (_state)
        {
            if (_exposureState == CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("An exposure is already running.");
            }

            _exposureState = CameraExposureState.Exposing;
            _exposureDuration = duration;
            _exposureElapsed = TimeSpan.Zero;
        }

        using var scope = BeginScope();
        var clock = Stopwatch.StartNew();
        var completed = false;
        var announced = false;
        var imageReady = false;
        try
        {
            Logger.LogInformation("Exposing {Device} for {Seconds:0.###} s", Name, duration.TotalSeconds);
            announced = true;
            await PublishAsync(
                new CameraExposureStateChanged(Id, CameraExposureState.Idle, CameraExposureState.Exposing), cancellationToken);
            RaiseProgress();

            clock.Restart();
            await CallAsync("start the exposure of", d => d.StartExposure(duration.TotalSeconds, frameType is FrameType.Light or FrameType.Flat), cancellationToken);

            var limit = duration + Timings.CameraDownloadMargin;
            while (true)
            {
                await Task.Delay(Timings.CameraPollInterval, cancellationToken);
                SetElapsed(clock.Elapsed < duration ? clock.Elapsed : duration);

                var (ready, state) = await CallAsync("poll", d => (d.ImageReady, d.CameraState), cancellationToken);
                if (ready)
                {
                    imageReady = true;
                    break;
                }

                if (state == AscomCameraState.Error)
                {
                    throw new AscomDeviceException(
                        Id, ProgId, "expose", $"{Name} reports an error state and did not deliver an image.");
                }

                if (clock.Elapsed > limit)
                {
                    var aborted = await AbortAsync();
                    throw new AscomTimeoutException(
                        Id, ProgId, "expose",
                        $"{Name} did not deliver an image within {limit.TotalSeconds:0.#} s. " +
                        (aborted
                            ? "The driver accepted AbortExposure and the camera reports that it is idle."
                            : "The exposure could not be confirmed stopped: the camera may still be exposing."));
                }
            }

            SetElapsed(duration);
            var (array, numX, numY) = await CallAsync("read the image of", d => (d.ImageArray, d.NumX, d.NumY), cancellationToken);
            var converted = Convert(array, numX, numY);
            completed = true;
            lock (_state)
            {
                _exposed = true;
            }

            Logger.LogInformation("{Device} delivered a frame of {Width} x {Height}", Name, converted.Width, converted.Height);
            return new CameraFrame(converted.Width, converted.Height, converted.Pixels, duration) { Acquisition = Describe(frameType) };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogInformation("Exposure of {Device} cancelled; asking the driver to abort", Name);
            var aborted = await AbortAsync();
            Logger.LogInformation(
                aborted
                    ? "{Device} confirmed idle after the cancelled exposure"
                    : "{Device} could not be confirmed stopped after the cancelled exposure; it may still be exposing",
                Name);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
            Logger.LogError(ex, "Exposure of {Device} failed", Name);
            if (!imageReady)
            {
                await AbortAsync();
            }

            throw;
        }
        finally
        {
            if (!completed)
            {
                // Keep the portion that really elapsed instead of jumping to 100 %.
                SetElapsed(clock.Elapsed < duration ? clock.Elapsed : duration);
            }

            lock (_state)
            {
                _exposureState = CameraExposureState.Idle;
            }

            if (announced)
            {
                await PublishAsync(new CameraExposureStateChanged(Id, CameraExposureState.Exposing, CameraExposureState.Idle));
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // What the frame was taken with: the settings the camera reported, with the names of list gains, offsets and readout modes.
    private FrameAcquisition Describe(FrameType frameType)
    {
        var s = Settings;
        var c = Capabilities.Value;
        string? Name(IntegerControl? control, int? value) =>
            control is { IsList: true } && value is { } v && v >= 0 && v < control.Choices.Count ? control.Choices[v] : null;

        return new FrameAcquisition
        {
            FrameType = frameType,
            Gain = s?.Gain,
            GainName = Name(c?.Gain, s?.Gain),
            Offset = s?.Offset,
            OffsetName = Name(c?.Offset, s?.Offset),
            BinX = s?.BinX,
            BinY = s?.BinY,
            StartX = s?.StartX,
            StartY = s?.StartY,
            Width = s?.NumX,
            Height = s?.NumY,
            ReadoutMode = c is not null && s?.ReadoutMode is { } r && r >= 0 && r < c.ReadoutModes.Count ? c.ReadoutModes[r] : null,
            FastReadout = s?.FastReadout,
        };
    }

    private ConvertedImage Convert(object? array, int numX, int numY)
    {
        try
        {
            var converted = AscomImageConverter.Convert(array, numX, numY);
            if (converted.Clamped > 0)
            {
                Logger.LogWarning(
                    "{Device}: {Clamped} of {Total} pixel values were outside 0 to 65535 and were clamped",
                    Name, converted.Clamped, (long)converted.Width * converted.Height);
            }

            return converted;
        }
        catch (ImageConversionException ex)
        {
            Logger.LogError("{Device}: the image was rejected. {Detail}", Name, ex.Detail);
            throw new AscomDeviceException(Id, ProgId, "expose", $"{Name} ({ProgId}): {ex.Message}", ex);
        }
    }

    // AbortExposure when the camera can, then watch for idle for a bounded time. True only when the camera itself says
    // it is idle after an abort the driver accepted.
    private async Task<bool> AbortAsync()
    {
        bool canAbort;
        lock (_state)
        {
            canAbort = _canAbort;
        }

        if (!canAbort)
        {
            Logger.LogWarning("{Device} cannot abort an exposure; whatever it is doing, Astra cannot stop it", Name);
            return false;
        }

        try
        {
            await CallAsync("abort the exposure of", d => d.AbortExposure(), CancellationToken.None).WaitAsync(Timings.StopWait);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: AbortExposure did not work: {Reason}", Name, ex is AscomDeviceException ? ex.Message : AscomErrors.Describe(ex));
            return false;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timings.StopWait)
        {
            try
            {
                var state = await CallAsync("poll", d => d.CameraState, CancellationToken.None).WaitAsync(Timings.StopWait);
                if (state is AscomCameraState.Idle)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("{Device}: could not tell whether the camera stopped: {Reason}", Name, ex.Message);
                return false;
            }

            await Task.Delay(Timings.CameraPollInterval);
        }

        return false;
    }

    private void SetElapsed(TimeSpan elapsed)
    {
        lock (_state)
        {
            _exposureElapsed = elapsed;
        }

        RaiseProgress();
    }

    // Progress observers must not be able to break an exposure.
    private void RaiseProgress()
    {
        foreach (var handler in ExposureProgressChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "A progress observer of {Device} threw", Name);
            }
        }
    }
}
