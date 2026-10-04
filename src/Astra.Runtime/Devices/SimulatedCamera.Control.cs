using Astra.Core.Devices;

namespace Astra.Runtime.Devices;

/// <summary>
/// The capability, settings and telemetry side of the simulated camera. It says what it really simulates, through the same
/// model as every other camera: binning and the subframe change the frame (the synthetic sky is binned and cropped), the
/// gain and the offset are accepted and remembered but do not alter the synthetic pixels (the capabilities say so in their
/// notes), and the cooler moves the sensor temperature towards the target like a first-order system.
/// </summary>
public sealed partial class SimulatedCamera
{
    public const double AmbientTemperature = 20;
    private const double CoolingTimeConstantSeconds = 40;
    private const double MinimumSensorTemperature = -30;

    private readonly object _control = new();
    private DeviceCapabilities<CameraCapabilities> _capabilities = DeviceCapabilities<CameraCapabilities>.Unknown;
    private CameraSettings? _settings;
    private double _temperatureAtChange = AmbientTemperature;
    private DateTimeOffset _temperatureChangedAt = DateTimeOffset.UtcNow;

    public DeviceCapabilities<CameraCapabilities> Capabilities
    {
        get { lock (_control) { return _capabilities; } }
    }

    public event EventHandler? CapabilitiesChanged;

    public event EventHandler? StateChanged;

    public CameraSettings? Settings
    {
        get { lock (_control) { return _settings; } }
    }

    public CameraTelemetry? Telemetry
    {
        get
        {
            lock (_control)
            {
                return _settings is null ? null : ReadTelemetry();
            }
        }
    }

    private static CameraCapabilities BuildCapabilities() => new()
    {
        Driver = new DriverMetadata("Astra simulated camera", "A camera that only pretends", "Astra.Runtime", "1.0", null),
        CanAbortExposure = true,
        CanStopExposure = false,
        MinExposureSeconds = 0.001,
        MaxExposureSeconds = 3600,
        ExposureResolutionSeconds = 0.001,
        SensorWidth = SyntheticFrameGenerator.Width,
        SensorHeight = SyntheticFrameGenerator.Height,
        PixelSizeXMicrons = 3.76,
        PixelSizeYMicrons = 3.76,
        MaxAdu = ushort.MaxValue,
        SensorType = SensorKind.Monochrome,
        SensorName = "Simulated monochrome sensor",
        HasShutter = false,
        MaxBinX = 4,
        MaxBinY = 4,
        CanAsymmetricBin = false,
        SupportsSubframe = true,
        Gain = IntegerControl.Range(0, 100),
        Offset = IntegerControl.Range(0, 255),
        ReadoutModes = ["Normal", "Slow"],
        CanFastReadout = false,
        CanSetCcdTemperature = true,
        HasCooler = true,
        CanGetCoolerPower = true,
        HasCcdTemperature = true,
        HasHeatSinkTemperature = true,
        Notes = ["Gain, offset and readout mode are remembered; the synthetic pixels do not depend on them."],
    };

    private void OnConnectionStateSet(DeviceConnectionState state)
    {
        if (state == DeviceConnectionState.Connected)
        {
            var capabilities = BuildCapabilities();
            lock (_control)
            {
                _capabilities = DeviceCapabilities<CameraCapabilities>.Of(capabilities);

                // Cooling does not survive a disconnect: the simulated camera starts again at ambient, cooler off.
                _settings ??= new CameraSettings();
                _settings = new CameraSettings
                {
                    Gain = 0,
                    Offset = 0,
                    BinX = 1,
                    BinY = 1,
                    StartX = 0,
                    StartY = 0,
                    NumX = capabilities.SensorWidth,
                    NumY = capabilities.SensorHeight,
                    ReadoutMode = 0,
                    TargetTemperature = AmbientTemperature,
                    CoolerOn = false,
                };
                _temperatureAtChange = AmbientTemperature;
                _temperatureChangedAt = DateTimeOffset.UtcNow;
            }

            CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (state == DeviceConnectionState.Disconnected)
        {
            lock (_control)
            {
                _capabilities = DeviceCapabilities<CameraCapabilities>.Unknown;
                _settings = null;
            }

            CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_control)
        {
            if (_settings is null)
            {
                throw new InvalidOperationException($"{Name} is not connected.");
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ApplyAsync(CameraSettings change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_control)
        {
            if (_capabilities.Value is not { } capabilities || _settings is null)
            {
                throw new InvalidOperationException($"{Name} is not connected.");
            }

            if (change.IsEmpty)
            {
                return Task.CompletedTask;
            }

            var needsIdle = change.Gain is not null || change.Offset is not null || change.BinX is not null || change.BinY is not null
                || change.ChangesSubframe || change.ReadoutMode is not null || change.FastReadout is not null;
            if (needsIdle && ExposureState == CameraExposureState.Exposing)
            {
                throw new InvalidOperationException("The camera is exposing; its settings can be changed when the exposure is over.");
            }

            var problems = CameraSettingsRules.Problems(capabilities, _settings, change);
            if (problems.Count > 0)
            {
                throw new ArgumentException(string.Join(" ", problems), nameof(change));
            }

            var next = _settings;
            if (change.BinX is not null || change.BinY is not null)
            {
                // A new binning means the whole sensor at that binning, as with every other camera.
                var bx = change.BinX ?? _settings.BinX ?? 1;
                var by = change.BinY ?? _settings.BinY ?? 1;
                next = next with
                {
                    BinX = bx,
                    BinY = by,
                    StartX = 0,
                    StartY = 0,
                    NumX = capabilities.SensorWidth / bx,
                    NumY = capabilities.SensorHeight / by,
                };
            }

            if (change.ChangesSubframe)
            {
                next = next with
                {
                    StartX = change.StartX ?? next.StartX,
                    StartY = change.StartY ?? next.StartY,
                    NumX = change.NumX ?? next.NumX,
                    NumY = change.NumY ?? next.NumY,
                };
            }

            next = next with
            {
                Gain = change.Gain ?? next.Gain,
                Offset = change.Offset ?? next.Offset,
                ReadoutMode = change.ReadoutMode ?? next.ReadoutMode,
                FastReadout = change.FastReadout ?? next.FastReadout,
            };

            if (change.TargetTemperature is { } target)
            {
                var now = DateTimeOffset.UtcNow;
                _temperatureAtChange = CurrentTemperature(now);
                _temperatureChangedAt = now;
                next = next with { TargetTemperature = target };
            }

            if (change.CoolerOn is { } on)
            {
                var now = DateTimeOffset.UtcNow;
                _temperatureAtChange = CurrentTemperature(now);
                _temperatureChangedAt = now;
                next = next with { CoolerOn = on };
            }

            _settings = next;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    // First-order approach to the goal: the target while the cooler is on, ambient while it is off.
    private double CurrentTemperature(DateTimeOffset now)
    {
        var goal = _settings is { CoolerOn: true, TargetTemperature: { } t }
            ? Math.Max(t, MinimumSensorTemperature)
            : AmbientTemperature;
        var elapsed = Math.Max(0, (now - _temperatureChangedAt).TotalSeconds);
        return goal + (_temperatureAtChange - goal) * Math.Exp(-elapsed / CoolingTimeConstantSeconds);
    }

    private CameraTelemetry ReadTelemetry()
    {
        var now = DateTimeOffset.UtcNow;
        var temperature = CurrentTemperature(now);
        var cooling = _settings is { CoolerOn: true };
        var power = cooling ? Math.Clamp((AmbientTemperature - temperature) / (AmbientTemperature - MinimumSensorTemperature) * 100 + 10, 0, 100) : 0;
        return new CameraTelemetry
        {
            CcdTemperature = temperature,
            CoolerPower = power,
            HeatSinkTemperature = AmbientTemperature + power * 0.1,
            CoolerOn = cooling,
            Time = now,
        };
    }

    // The frame the camera delivers is the synthetic sky, binned and cropped to the settings; with the default settings it
    // is returned untouched.
    private CameraFrame ApplyGeometry(CameraFrame frame)
    {
        CameraSettings? settings;
        lock (_control)
        {
            settings = _settings;
        }

        if (settings is null)
        {
            return frame;
        }

        var bx = settings.BinX ?? 1;
        var by = settings.BinY ?? 1;
        var startX = settings.StartX ?? 0;
        var startY = settings.StartY ?? 0;
        var numX = settings.NumX ?? frame.Width / bx;
        var numY = settings.NumY ?? frame.Height / by;
        if (bx == 1 && by == 1 && startX == 0 && startY == 0 && numX == frame.Width && numY == frame.Height)
        {
            return frame;
        }

        var pixels = new ushort[numX * numY];
        var source = frame.Pixels.Span;
        for (var y = 0; y < numY; y++)
        {
            for (var x = 0; x < numX; x++)
            {
                long sum = 0;
                for (var dy = 0; dy < by; dy++)
                {
                    for (var dx = 0; dx < bx; dx++)
                    {
                        sum += source[((startY + y) * by + dy) * frame.Width + (startX + x) * bx + dx];
                    }
                }

                pixels[y * numX + x] = (ushort)(sum / (bx * by));
            }
        }

        return new CameraFrame(numX, numY, pixels, frame.ExposureDuration);
    }
}
