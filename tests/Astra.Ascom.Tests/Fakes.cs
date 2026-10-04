using System.Collections.Concurrent;
using System.Globalization;
using Astra.Ascom.Drivers;
using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Mounts;

namespace Astra.Ascom.Tests;

/// <summary>One call a fake driver received, with the thread it came on.</summary>
public sealed record DriverCall(string Name, int ThreadId, ApartmentState Apartment);

/// <summary>The calls of one or several fake drivers, in order, with the thread of each.</summary>
public sealed class CallLog
{
    private readonly ConcurrentQueue<DriverCall> _calls = new();

    public IReadOnlyList<DriverCall> Calls => _calls.ToArray();

    public IReadOnlyList<string> Names => _calls.Select(c => c.Name).ToArray();

    public int Count(string name) => _calls.Count(c => c.Name == name);

    public void Add(string name) =>
        _calls.Enqueue(new DriverCall(name, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

    /// <summary>Every call came on one single-threaded-apartment thread, the same for all.</summary>
    public void AssertOneStaThread()
    {
        var calls = Calls;
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Equal(ApartmentState.STA, c.Apartment));
        Assert.Single(calls.Select(c => c.ThreadId).Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, calls[0].ThreadId);
    }
}

/// <summary>Collects the events an adapter publishes.</summary>
public sealed class EventSink : IEventPublisher
{
    private readonly ConcurrentQueue<IAstraEvent> _events = new();

    public IReadOnlyList<IAstraEvent> Events => _events.ToArray();

    public IEnumerable<T> Of<T>() where T : IAstraEvent => Events.OfType<T>();

    public Task PublishAsync<TEvent>(TEvent astraEvent, CancellationToken cancellationToken = default) where TEvent : IAstraEvent
    {
        _events.Enqueue(astraEvent);
        return Task.CompletedTask;
    }
}

public abstract class FakeDriver(CallLog log) : IAscomDriver
{
    private bool _connected;

    public CallLog Log { get; } = log;
    public bool Disposed { get; private set; }
    public Exception? ConnectThrows { get; set; }
    public TimeSpan ConnectBlocks { get; set; }
    public Exception? SetupThrows { get; set; }

    public DriverMetadata Identity { get; set; } = new("Fake driver", "A fake driver for tests", "Fake driver info", "1.2.3", 3);

    public bool Connected
    {
        get
        {
            Log.Add("get Connected");
            return _connected;
        }
        set
        {
            Log.Add(value ? "Connected = true" : "Connected = false");
            if (value)
            {
                if (ConnectBlocks > TimeSpan.Zero)
                {
                    Thread.Sleep(ConnectBlocks);
                }

                if (ConnectThrows is not null)
                {
                    throw ConnectThrows;
                }
            }

            _connected = value;
        }
    }

    public void SetupDialog()
    {
        Log.Add("SetupDialog");
        if (SetupThrows is not null)
        {
            throw SetupThrows;
        }
    }

    public void Dispose()
    {
        Log.Add("Dispose");
        Disposed = true;
    }
}

public sealed class FakeFocuserDriver(CallLog log) : FakeDriver(log), IAscomFocuserDriver
{
    private readonly object _gate = new();
    private int _target;
    private int _start;
    private int _polls;
    private bool _moving;

    public bool Absolute { get; set; } = true;
    public int MaxStep { get; set; } = 50000;
    public int PositionValue { get; set; } = 25000;

    /// <summary>How many times IsMoving is asked before the focuser has arrived.</summary>
    public int MovePolls { get; set; } = 2;

    /// <summary>The focuser keeps moving until <see cref="Arrive"/> is called.</summary>
    public bool HoldMove { get; set; }

    public Exception? MoveThrows { get; set; }
    public Exception? HaltThrows { get; set; }

    /// <summary>Halt really stops the focuser, half way.</summary>
    public bool HaltStops { get; set; } = true;

    public Exception? PositionThrowsWhileMoving { get; set; }

    /// <summary>After a Halt that stops the focuser, the first this many position reads still say where it was (the EAF does this).</summary>
    public int StalePositionReadsAfterHalt { get; set; }

    private int _staleReads;
    private int _stalePosition;

    public int MaxIncrementValue { get; set; } = 50000;
    public double? StepSizeValue { get; set; } = 1.5;

    /// <summary>The temperature; null means the focuser has no probe and the driver says not implemented.</summary>
    public double? TemperatureValue { get; set; }

    public bool TempCompAvailableValue { get; set; }
    public bool TempCompValue { get; set; }

    public int MaxIncrement => MaxIncrementValue;

    public double StepSize => StepSizeValue ?? throw new NotImplementedException("StepSize");

    public double Temperature => TemperatureValue ?? throw new NotImplementedException("Temperature");

    public bool TempCompAvailable => TempCompAvailableValue;

    public bool TempComp
    {
        get => TempCompAvailableValue ? TempCompValue : throw new NotImplementedException("TempComp");
        set
        {
            Log.Add($"TempComp = {value}");
            if (!TempCompAvailableValue)
            {
                throw new NotImplementedException("TempComp");
            }

            TempCompValue = value;
        }
    }

    public int Position
    {
        get
        {
            Log.Add("get Position");
            if (!Absolute)
            {
                throw new NotImplementedException("Position of a relative focuser");
            }

            lock (_gate)
            {
                if (_moving && PositionThrowsWhileMoving is not null)
                {
                    throw PositionThrowsWhileMoving;
                }

                if (_staleReads > 0)
                {
                    _staleReads--;
                    return _stalePosition;
                }

                return PositionValue;
            }
        }
    }

    public bool IsMoving
    {
        get
        {
            Log.Add("get IsMoving");
            lock (_gate)
            {
                if (_moving && !HoldMove && ++_polls >= MovePolls)
                {
                    _moving = false;
                    PositionValue = _target;
                }

                return _moving;
            }
        }
    }

    public void Move(int position)
    {
        Log.Add($"Move {position}");
        if (MoveThrows is not null)
        {
            throw MoveThrows;
        }

        lock (_gate)
        {
            (_start, _target, _polls, _moving) = (PositionValue, Absolute ? position : PositionValue + position, 0, true);
        }
    }

    public void Halt()
    {
        Log.Add("Halt");
        if (HaltThrows is not null)
        {
            throw HaltThrows;
        }

        lock (_gate)
        {
            if (HaltStops && _moving)
            {
                _moving = false;
                (_stalePosition, _staleReads) = (PositionValue, StalePositionReadsAfterHalt);
                PositionValue = (_start + _target) / 2;
            }
        }
    }

    public void Arrive()
    {
        lock (_gate)
        {
            _moving = false;
            PositionValue = _target;
        }
    }
}

public sealed class FakeMountDriver(CallLog log) : FakeDriver(log), IAscomMountDriver
{
    private readonly object _gate = new();
    private (double Ra, double Dec) _target;
    private int _polls;
    private bool _slewing;

    public bool CanSlew { get; set; } = true;
    public bool CanSlewAsync { get; set; } = true;
    public bool CanSetTracking { get; set; } = true;
    public bool AtParkValue { get; set; }

    public bool CanSlewAltAz { get; set; }
    public bool CanSlewAltAzAsync { get; set; }
    public bool CanSync { get; set; }
    public bool CanSyncAltAz { get; set; }
    public bool CanPark { get; set; }
    public bool CanUnpark { get; set; }
    public bool CanSetPark { get; set; }
    public bool CanFindHome { get; set; }
    public bool CanSetPierSide { get; set; }
    public bool CanPulseGuide { get; set; }
    public bool CanSetGuideRates { get; set; }
    public bool CanSetRightAscensionRate { get; set; }
    public bool CanSetDeclinationRate { get; set; }
    public HashSet<MountAxis> MovableAxes { get; } = [];
    public IReadOnlyList<AxisRate> Rates { get; set; } = [new AxisRate(0.01, 2)];
    public bool AtHomeValue { get; set; }
    public bool PulseGuidingValue { get; set; }
    public TrackingRate TrackingRateValue { get; set; } = TrackingRate.Sidereal;
    public IReadOnlyList<TrackingRate> TrackingRatesValue { get; set; } = [TrackingRate.Sidereal, TrackingRate.Lunar];
    public PierSide PierSideValue { get; set; } = PierSide.East;
    public bool PredictsPierSide { get; set; }
    public bool HasAltAzValue { get; set; } = true;
    public bool HasSiteValue { get; set; } = true;
    public double? SiderealTimeValue { get; set; } = 5.5;
    public bool? RefractionValue { get; set; }
    public (double Rate, double Dec)? GuideRatesValue { get; set; }
    public EquatorialSystemKind EquatorialSystemValue { get; set; } = EquatorialSystemKind.Topocentric;
    public AlignmentKind AlignmentValue { get; set; } = AlignmentKind.GermanPolar;
    public Exception? PulseThrows { get; set; }
    public Exception? MoveAxisThrows { get; set; }
    public List<string> Operations { get; } = [];

    public bool AtHome => CanFindHome ? AtHomeValue : throw new NotImplementedException("AtHome");
    public bool IsPulseGuiding => PulseGuidingValue;
    public bool CanMoveAxis(MountAxis axis) => MovableAxes.Contains(axis);
    public IReadOnlyList<AxisRate> AxisRates(MountAxis axis) => MovableAxes.Contains(axis) ? Rates : throw new NotImplementedException("AxisRates");
    public double Altitude => HasAltAzValue ? 40 : throw new NotImplementedException("Altitude");
    public double Azimuth => HasAltAzValue ? 120 : throw new NotImplementedException("Azimuth");

    public TrackingRate TrackingRate
    {
        get => TrackingRatesValue.Count > 0 ? TrackingRateValue : throw new NotImplementedException("TrackingRate");
        set
        {
            Log.Add($"TrackingRate = {value}");
            TrackingRateValue = value;
        }
    }

    public IReadOnlyList<TrackingRate> TrackingRates => TrackingRatesValue;
    public PierSide SideOfPier => PierSideValue;

    public PierSide DestinationSideOfPier(double rightAscensionHours, double declinationDegrees)
    {
        Log.Add("DestinationSideOfPier");
        return PredictsPierSide ? PierSideValue : throw new NotImplementedException("DestinationSideOfPier");
    }

    public double SiderealTime => SiderealTimeValue ?? throw new NotImplementedException("SiderealTime");
    public DateTime UtcDate => new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public double GuideRateRightAscension
    {
        get => GuideRatesValue?.Rate ?? throw new NotImplementedException("GuideRateRightAscension");
        set
        {
            Log.Add("GuideRateRightAscension set");
            GuideRatesValue = (value, GuideRatesValue?.Dec ?? value);
        }
    }

    public double GuideRateDeclination
    {
        get => GuideRatesValue?.Dec ?? throw new NotImplementedException("GuideRateDeclination");
        set
        {
            Log.Add("GuideRateDeclination set");
            GuideRatesValue = (GuideRatesValue?.Rate ?? value, value);
        }
    }

    public bool DoesRefraction
    {
        get => RefractionValue ?? throw new NotImplementedException("DoesRefraction");
        set
        {
            Log.Add($"DoesRefraction = {value}");
            RefractionValue = value;
        }
    }

    public EquatorialSystemKind EquatorialSystem => EquatorialSystemValue;
    public AlignmentKind AlignmentMode => AlignmentValue;
    public double ApertureArea => 0.01;
    public double ApertureDiameter => 0.1;
    public double FocalLength => 0.5;
    public double SiteLatitude => HasSiteValue ? 50.1 : throw new NotImplementedException("SiteLatitude");
    public double SiteLongitude => HasSiteValue ? 8.6 : throw new NotImplementedException("SiteLongitude");
    public double SiteElevation => HasSiteValue ? 120 : throw new NotImplementedException("SiteElevation");

    public void SlewToAltAz(double altitudeDegrees, double azimuthDegrees)
    {
        Log.Add("SlewToAltAz");
        Operations.Add("SlewToAltAz");
        Thread.Sleep(BlockingSlewTime);
    }

    public void SlewToAltAzAsync(double altitudeDegrees, double azimuthDegrees)
    {
        Log.Add("SlewToAltAzAsync");
        Operations.Add("SlewToAltAzAsync");
        lock (_gate)
        {
            (_target, _polls, _slewing) = ((Ra, Dec), 0, true);
        }
    }

    public void SyncToCoordinates(double rightAscensionHours, double declinationDegrees)
    {
        Log.Add("SyncToCoordinates");
        Operations.Add("SyncToCoordinates");
        (Ra, Dec) = (rightAscensionHours, declinationDegrees);
    }

    public void Park()
    {
        Log.Add("Park");
        Operations.Add("Park");
        lock (_gate)
        {
            (_target, _polls, _slewing) = ((Ra, Dec), 0, true);
            AtParkValue = true;
        }
    }

    public void Unpark()
    {
        Log.Add("Unpark");
        Operations.Add("Unpark");
        AtParkValue = false;
    }

    public void SetPark()
    {
        Log.Add("SetPark");
        Operations.Add("SetPark");
    }

    public void FindHome()
    {
        Log.Add("FindHome");
        Operations.Add("FindHome");
        lock (_gate)
        {
            (_target, _polls, _slewing) = ((Ra, Dec), 0, true);
            AtHomeValue = true;
        }
    }

    public void PulseGuide(GuideDirection direction, int durationMilliseconds)
    {
        Log.Add($"PulseGuide {direction} {durationMilliseconds}");
        Operations.Add($"PulseGuide {direction} {durationMilliseconds}");
        if (PulseThrows is not null)
        {
            throw PulseThrows;
        }
    }

    public void MoveAxis(MountAxis axis, double degreesPerSecond)
    {
        Log.Add($"MoveAxis {axis} {degreesPerSecond}");
        Operations.Add($"MoveAxis {axis} {degreesPerSecond}");
        if (MoveAxisThrows is not null)
        {
            throw MoveAxisThrows;
        }
    }

    public bool AtPark
    {
        get
        {
            Log.Add("get AtPark");
            return AtParkValue;
        }
    }

    public bool TrackingValue { get; set; } = true;
    public double Ra { get; set; } = 3;
    public double Dec { get; set; } = 10;
    public int SlewPolls { get; set; } = 2;
    public bool HoldSlew { get; set; }
    public TimeSpan BlockingSlewTime { get; set; } = TimeSpan.FromMilliseconds(30);
    public Exception? SlewThrows { get; set; }
    public Exception? AbortThrows { get; set; }
    public bool AbortStops { get; set; } = true;

    public bool Slewing
    {
        get
        {
            Log.Add("get Slewing");
            lock (_gate)
            {
                if (_slewing && !HoldSlew && ++_polls >= SlewPolls)
                {
                    _slewing = false;
                    (Ra, Dec) = _target;
                }

                return _slewing || AlwaysReportsSlewing;
            }
        }
    }

    /// <summary>A driver that says it is slewing although nothing was started (seen with a mount that is not powered).</summary>
    public bool AlwaysReportsSlewing { get; set; }

    /// <summary>The driver accepts a change of tracking without reaching it (the state read back stays as it was).</summary>
    public bool IgnoresTrackingChange { get; set; }

    public bool Tracking
    {
        get
        {
            Log.Add("get Tracking");
            return TrackingValue;
        }
        set
        {
            Log.Add($"Tracking = {value}");
            if (!IgnoresTrackingChange)
            {
                TrackingValue = value;
            }
        }
    }

    public double RightAscension
    {
        get
        {
            Log.Add("get RightAscension");
            return Ra;
        }
    }

    public double Declination
    {
        get
        {
            Log.Add("get Declination");
            return Dec;
        }
    }

    public void SlewToCoordinates(double rightAscensionHours, double declinationDegrees)
    {
        Log.Add(string.Create(CultureInfo.InvariantCulture, $"SlewToCoordinates {rightAscensionHours:0.###} {declinationDegrees:0.###}"));
        if (SlewThrows is not null)
        {
            throw SlewThrows;
        }

        Thread.Sleep(BlockingSlewTime);
        (Ra, Dec) = (rightAscensionHours, declinationDegrees);
    }

    public void SlewToCoordinatesAsync(double rightAscensionHours, double declinationDegrees)
    {
        Log.Add(string.Create(CultureInfo.InvariantCulture, $"SlewToCoordinatesAsync {rightAscensionHours:0.###} {declinationDegrees:0.###}"));
        if (SlewThrows is not null)
        {
            throw SlewThrows;
        }

        lock (_gate)
        {
            (_target, _polls, _slewing) = ((rightAscensionHours, declinationDegrees), 0, true);
        }
    }

    public void AbortSlew()
    {
        Log.Add("AbortSlew");
        Operations.Add("AbortSlew");
        if (AbortThrows is not null)
        {
            throw AbortThrows;
        }

        lock (_gate)
        {
            if (AbortStops && _slewing)
            {
                _slewing = false;
                (Ra, Dec) = ((Ra + _target.Ra) / 2, (Dec + _target.Dec) / 2);
            }
        }
    }

    public void Arrive()
    {
        lock (_gate)
        {
            _slewing = false;
            (Ra, Dec) = _target;
        }
    }
}

public sealed class FakeCameraDriver(CallLog log) : FakeDriver(log), IAscomCameraDriver
{
    private readonly object _gate = new();
    private int _polls;
    private bool _exposing;

    public int CameraXSize { get; set; } = 4;
    public int CameraYSize { get; set; } = 3;
    public int NumX { get; set; } = 4;
    public int NumY { get; set; } = 3;
    public int StartX { get; set; }
    public int StartY { get; set; }
    public int BinX { get; set; } = 1;
    public int BinY { get; set; } = 1;
    public int MaxBinX { get; set; } = 1;
    public int MaxBinY { get; set; } = 1;
    public bool CanAsymmetricBin { get; set; }
    public bool CanStopExposure { get; set; }
    public double ExposureMin { get; set; } = 0.001;
    public double ExposureMax { get; set; } = 3600;
    public double ExposureResolution { get; set; } = 0.001;
    public double PixelSizeX { get; set; } = 3.76;
    public double PixelSizeY { get; set; } = 3.76;
    public double ElectronsPerAdu { get; set; } = 1;
    public double FullWellCapacity { get; set; } = 50000;
    public SensorKind SensorType { get; set; } = SensorKind.Monochrome;
    public string SensorName { get; set; } = "Fake sensor";
    public int BayerOffsetX { get; set; }
    public int BayerOffsetY { get; set; }
    public bool HasShutter { get; set; }
    public double LastExposureDuration { get; set; } = 1;
    public string LastExposureStartTime { get; set; } = "2026-01-02T03:04:05";

    // null means the driver does not implement the member and says so.
    public int? GainValue { get; set; }
    public int? GainMinValue { get; set; }
    public int? GainMaxValue { get; set; }
    public IReadOnlyList<string>? GainsValue { get; set; }
    public int? OffsetValue { get; set; }
    public int? OffsetMinValue { get; set; }
    public int? OffsetMaxValue { get; set; }
    public IReadOnlyList<string>? OffsetsValue { get; set; }
    public IReadOnlyList<string> ReadoutModesValue { get; set; } = [];
    public int ReadoutModeValue { get; set; }
    public bool CanFastReadout { get; set; }
    public bool FastReadoutValue { get; set; }
    public bool CanSetCcdTemperature { get; set; }
    public bool CanGetCoolerPower { get; set; }
    public double? CcdTemperatureValue { get; set; }
    public double SetCcdTemperatureValue { get; set; } = 20;
    public double? CoolerPowerValue { get; set; }
    public double? HeatSinkTemperatureValue { get; set; }
    public bool? CoolerOnValue { get; set; }
    public List<string> Changes { get; } = [];
    public Exception? GainSetThrows { get; set; }

    public int Gain
    {
        get => GainValue ?? throw new NotImplementedException("Gain");
        set
        {
            Log.Add($"Gain = {value}");
            Changes.Add($"Gain = {value}");
            if (GainSetThrows is not null)
            {
                throw GainSetThrows;
            }

            GainValue = value;
        }
    }

    public int GainMin => GainMinValue ?? throw new NotImplementedException("GainMin");
    public int GainMax => GainMaxValue ?? throw new NotImplementedException("GainMax");
    public IReadOnlyList<string> Gains => GainsValue ?? throw new NotImplementedException("Gains");

    public int Offset
    {
        get => OffsetValue ?? throw new NotImplementedException("Offset");
        set
        {
            Log.Add($"Offset = {value}");
            Changes.Add($"Offset = {value}");
            OffsetValue = value;
        }
    }

    public int OffsetMin => OffsetMinValue ?? throw new NotImplementedException("OffsetMin");
    public int OffsetMax => OffsetMaxValue ?? throw new NotImplementedException("OffsetMax");
    public IReadOnlyList<string> Offsets => OffsetsValue ?? throw new NotImplementedException("Offsets");

    public int ReadoutMode
    {
        get => ReadoutModesValue.Count > 0 ? ReadoutModeValue : throw new NotImplementedException("ReadoutMode");
        set
        {
            Changes.Add($"ReadoutMode = {value}");
            ReadoutModeValue = value;
        }
    }

    public IReadOnlyList<string> ReadoutModes => ReadoutModesValue;

    public bool FastReadout
    {
        get => CanFastReadout ? FastReadoutValue : throw new NotImplementedException("FastReadout");
        set
        {
            Changes.Add($"FastReadout = {value}");
            FastReadoutValue = value;
        }
    }

    public double CcdTemperature => CcdTemperatureValue ?? throw new NotImplementedException("CCDTemperature");

    public double SetCcdTemperature
    {
        get => CanSetCcdTemperature ? SetCcdTemperatureValue : throw new NotImplementedException("SetCCDTemperature");
        set
        {
            Log.Add($"SetCCDTemperature = {value}");
            Changes.Add($"SetCCDTemperature = {value}");
            SetCcdTemperatureValue = value;
        }
    }

    public double CoolerPower => CoolerPowerValue ?? throw new NotImplementedException("CoolerPower");
    public double HeatSinkTemperature => HeatSinkTemperatureValue ?? throw new NotImplementedException("HeatSinkTemperature");

    public bool CoolerOn
    {
        get => CoolerOnValue ?? throw new NotImplementedException("CoolerOn");
        set
        {
            Log.Add($"CoolerOn = {value}");
            Changes.Add($"CoolerOn = {value}");
            CoolerOnValue = value;
        }
    }
    public int MaxAduValue { get; set; } = 65535;
    public bool CanAbort { get; set; } = true;
    public int ExposePolls { get; set; } = 2;
    public bool HoldExposure { get; set; }
    public Exception? StartThrows { get; set; }
    public Exception? AbortThrows { get; set; }
    public bool ErrorState { get; set; }
    public List<(double Seconds, bool Light)> Starts { get; } = [];

    /// <summary>What ImageArray returns; by default a 4 x 3 image whose pixel (x, y) has the value 10 * x + y.</summary>
    public object? Image { get; set; } = DefaultImage();

    public Exception? ImageThrows { get; set; }

    /// <summary>When set, the image is made at the time it is read (so that it can depend on the rest of the setup).</summary>
    public Func<object?>? ImageFactory { get; set; }

    public int MaxAdu
    {
        get
        {
            Log.Add("get MaxADU");
            return MaxAduValue;
        }
    }

    public bool CanAbortExposure
    {
        get
        {
            Log.Add("get CanAbortExposure");
            return CanAbort;
        }
    }

    public AscomCameraState CameraState
    {
        get
        {
            Log.Add("get CameraState");
            lock (_gate)
            {
                return ErrorState ? AscomCameraState.Error : _exposing ? AscomCameraState.Exposing : AscomCameraState.Idle;
            }
        }
    }

    public bool ImageReady
    {
        get
        {
            Log.Add("get ImageReady");
            lock (_gate)
            {
                if (_exposing && !HoldExposure && ++_polls >= ExposePolls)
                {
                    _exposing = false;
                    _ready = true;
                }

                return _ready;
            }
        }
    }

    private bool _ready;

    public object? ImageArray
    {
        get
        {
            Log.Add("get ImageArray");
            if (ImageThrows is not null)
            {
                throw ImageThrows;
            }

            return ImageFactory is { } factory ? factory() : Image;
        }
    }

    public static int[,] DefaultImage()
    {
        var image = new int[4, 3];
        for (var x = 0; x < 4; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                image[x, y] = 10 * x + y;
            }
        }

        return image;
    }

    public void StartExposure(double durationSeconds, bool light)
    {
        Log.Add(string.Create(CultureInfo.InvariantCulture, $"StartExposure {durationSeconds:0.###} light={light}"));
        if (StartThrows is not null)
        {
            throw StartThrows;
        }

        lock (_gate)
        {
            Starts.Add((durationSeconds, light));
            (_polls, _exposing, _ready) = (0, true, false);
        }
    }

    public void AbortExposure()
    {
        Log.Add("AbortExposure");
        if (AbortThrows is not null)
        {
            throw AbortThrows;
        }

        lock (_gate)
        {
            _exposing = false;
            _ready = false;
        }
    }
}

/// <summary>Hands out fake drivers, one new instance per request unless told otherwise, and remembers them.</summary>
public sealed class FakeDriverFactory(CallLog log) : IAscomDriverFactory
{
    public CallLog Log { get; } = log;
    public List<FakeFocuserDriver> Focusers { get; } = [];
    public List<FakeMountDriver> Mounts { get; } = [];
    public List<FakeCameraDriver> Cameras { get; } = [];
    public Action<FakeFocuserDriver>? ConfigureFocuser { get; set; }
    public Action<FakeMountDriver>? ConfigureMount { get; set; }
    public Action<FakeCameraDriver>? ConfigureCamera { get; set; }
    public Exception? CreateThrows { get; set; }

    public IAscomFocuserDriver CreateFocuser(string progId)
    {
        Log.Add($"create {progId}");
        if (CreateThrows is not null)
        {
            throw CreateThrows;
        }

        var driver = new FakeFocuserDriver(Log);
        ConfigureFocuser?.Invoke(driver);
        Focusers.Add(driver);
        return driver;
    }

    public IAscomMountDriver CreateMount(string progId)
    {
        Log.Add($"create {progId}");
        if (CreateThrows is not null)
        {
            throw CreateThrows;
        }

        var driver = new FakeMountDriver(Log);
        ConfigureMount?.Invoke(driver);
        Mounts.Add(driver);
        return driver;
    }

    public IAscomCameraDriver CreateCamera(string progId)
    {
        Log.Add($"create {progId}");
        if (CreateThrows is not null)
        {
            throw CreateThrows;
        }

        var driver = new FakeCameraDriver(Log);
        ConfigureCamera?.Invoke(driver);
        Cameras.Add(driver);
        return driver;
    }
}

/// <summary>Short timings, so that tests of polling and timeouts take milliseconds.</summary>
public static class FastTimings
{
    public static AscomTimings Create(
        TimeSpan? moveTimeout = null, TimeSpan? slewTimeout = null, TimeSpan? downloadMargin = null, TimeSpan? connectTimeout = null) =>
        new()
        {
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
            ReleaseTimeout = TimeSpan.FromSeconds(5),
            FocuserPollInterval = TimeSpan.FromMilliseconds(5),
            FocuserMoveTimeout = moveTimeout ?? TimeSpan.FromSeconds(10),
            MountPollInterval = TimeSpan.FromMilliseconds(5),
            MountSlewTimeout = slewTimeout ?? TimeSpan.FromSeconds(10),
            CameraPollInterval = TimeSpan.FromMilliseconds(5),
            CameraDownloadMargin = downloadMargin ?? TimeSpan.FromSeconds(10),
            StopWait = TimeSpan.FromMilliseconds(300),
            TrackingConfirmWait = TimeSpan.FromMilliseconds(100),
        };
}
