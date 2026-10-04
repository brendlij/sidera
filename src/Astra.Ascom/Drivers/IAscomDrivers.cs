using Astra.Core.Devices;
using Astra.Core.Mounts;

namespace Astra.Ascom.Drivers;

/// <summary>
/// The thin layer between Astra's adapters and an ASCOM driver: the members of the ASCOM interfaces that the adapters use,
/// named as ASCOM names them, with the types of Astra where ASCOM has an enumeration of its own (so that no ASCOM type
/// leaves this project). Every member is called on the dispatcher thread of the device that owns the driver instance,
/// never anywhere else. A member that the driver does not implement throws, as it does in ASCOM; the adapters probe the
/// optional ones and treat that as "not supported".
/// The adapters are tested against fakes of these interfaces; <see cref="ComAscomDriverFactory"/> is the real thing.
/// </summary>
public interface IAscomDriver : IDisposable
{
    /// <summary>The ASCOM Connected property.</summary>
    bool Connected { get; set; }

    /// <summary>What the driver says about itself (name, description, driver info, version, interface version).</summary>
    DriverMetadata Identity { get; }

    /// <summary>Shows the driver's own setup dialog (modal). The driver should not be connected.</summary>
    void SetupDialog();
}

public interface IAscomFocuserDriver : IAscomDriver
{
    /// <summary>True for a focuser that moves to absolute positions; false for one that only moves by a number of steps.</summary>
    bool Absolute { get; }

    /// <summary>The highest position; an absolute focuser moves between 0 and this.</summary>
    int MaxStep { get; }

    /// <summary>The most steps one move may be.</summary>
    int MaxIncrement { get; }

    /// <summary>Not implemented by every driver.</summary>
    double StepSize { get; }

    /// <summary>Not implemented by a relative focuser.</summary>
    int Position { get; }

    bool IsMoving { get; }

    /// <summary>The temperature in degrees Celsius; not every focuser has a probe.</summary>
    double Temperature { get; }

    bool TempCompAvailable { get; }

    bool TempComp { get; set; }

    /// <summary>For an absolute focuser the target position, for a relative one the number of steps.</summary>
    void Move(int positionOrSteps);

    void Halt();
}

public interface IAscomMountDriver : IAscomDriver
{
    // Capability flags
    bool CanSlew { get; }
    bool CanSlewAsync { get; }
    bool CanSlewAltAz { get; }
    bool CanSlewAltAzAsync { get; }
    bool CanSync { get; }
    bool CanSyncAltAz { get; }
    bool CanPark { get; }
    bool CanUnpark { get; }
    bool CanSetPark { get; }
    bool CanFindHome { get; }
    bool CanSetTracking { get; }
    bool CanSetPierSide { get; }
    bool CanPulseGuide { get; }
    bool CanSetGuideRates { get; }
    bool CanSetRightAscensionRate { get; }
    bool CanSetDeclinationRate { get; }

    bool CanMoveAxis(MountAxis axis);

    /// <summary>The ranges of rates an axis can be moved at, in degrees per second.</summary>
    IReadOnlyList<AxisRate> AxisRates(MountAxis axis);

    // State
    bool AtPark { get; }
    bool AtHome { get; }
    bool Slewing { get; }
    bool IsPulseGuiding { get; }
    bool Tracking { get; set; }

    /// <summary>Hours.</summary>
    double RightAscension { get; }

    /// <summary>Degrees.</summary>
    double Declination { get; }

    /// <summary>Degrees.</summary>
    double Altitude { get; }

    /// <summary>Degrees.</summary>
    double Azimuth { get; }

    TrackingRate TrackingRate { get; set; }

    IReadOnlyList<TrackingRate> TrackingRates { get; }

    PierSide SideOfPier { get; }

    PierSide DestinationSideOfPier(double rightAscensionHours, double declinationDegrees);

    double SiderealTime { get; }

    DateTime UtcDate { get; }

    double GuideRateRightAscension { get; set; }

    double GuideRateDeclination { get; set; }

    bool DoesRefraction { get; set; }

    // The mount itself
    EquatorialSystemKind EquatorialSystem { get; }
    AlignmentKind AlignmentMode { get; }
    double ApertureArea { get; }
    double ApertureDiameter { get; }
    double FocalLength { get; }
    double SiteLatitude { get; }
    double SiteLongitude { get; }
    double SiteElevation { get; }

    // Operations
    /// <summary>Slews and returns when the slew is over (the driver blocks).</summary>
    void SlewToCoordinates(double rightAscensionHours, double declinationDegrees);

    /// <summary>Starts a slew and returns at once; <see cref="Slewing"/> says when it is over.</summary>
    void SlewToCoordinatesAsync(double rightAscensionHours, double declinationDegrees);

    void SlewToAltAz(double altitudeDegrees, double azimuthDegrees);

    void SlewToAltAzAsync(double altitudeDegrees, double azimuthDegrees);

    void SyncToCoordinates(double rightAscensionHours, double declinationDegrees);

    void AbortSlew();

    void Park();

    void Unpark();

    void SetPark();

    void FindHome();

    void PulseGuide(GuideDirection direction, int durationMilliseconds);

    /// <summary>Degrees per second; 0 stops the axis.</summary>
    void MoveAxis(MountAxis axis, double degreesPerSecond);
}

/// <summary>The camera states of ASCOM, with the numbers ASCOM gives them.</summary>
public enum AscomCameraState
{
    Idle = 0,
    Waiting = 1,
    Exposing = 2,
    Reading = 3,
    Download = 4,
    Error = 5,
}

public interface IAscomCameraDriver : IAscomDriver
{
    // Sensor
    int CameraXSize { get; }
    int CameraYSize { get; }
    int MaxAdu { get; }
    double PixelSizeX { get; }
    double PixelSizeY { get; }
    double ElectronsPerAdu { get; }
    double FullWellCapacity { get; }
    SensorKind SensorType { get; }
    string SensorName { get; }
    int BayerOffsetX { get; }
    int BayerOffsetY { get; }
    bool HasShutter { get; }

    // The frame: size and position in binned pixels, binning
    int NumX { get; set; }
    int NumY { get; set; }
    int StartX { get; set; }
    int StartY { get; set; }
    int BinX { get; set; }
    int BinY { get; set; }
    int MaxBinX { get; }
    int MaxBinY { get; }
    bool CanAsymmetricBin { get; }

    // Exposure
    bool CanAbortExposure { get; }
    bool CanStopExposure { get; }
    double ExposureMin { get; }
    double ExposureMax { get; }
    double ExposureResolution { get; }
    AscomCameraState CameraState { get; }
    bool ImageReady { get; }
    double LastExposureDuration { get; }
    string LastExposureStartTime { get; }

    /// <summary>Starts an exposure; <paramref name="light"/> true is a light frame, false a dark one.</summary>
    void StartExposure(double durationSeconds, bool light);

    void AbortExposure();

    /// <summary>Ends the exposure now, but keeps what has been collected: the driver delivers an image, if it supports it (CanStopExposure).</summary>
    void StopExposure();

    /// <summary>The ASCOM ImageArray exactly as the driver returned it; the runtime type depends on the driver.</summary>
    object? ImageArray { get; }

    // Gain and offset: a range (min and max) or a list, never both
    int Gain { get; set; }
    int GainMin { get; }
    int GainMax { get; }
    IReadOnlyList<string> Gains { get; }
    int Offset { get; set; }
    int OffsetMin { get; }
    int OffsetMax { get; }
    IReadOnlyList<string> Offsets { get; }

    // Readout
    int ReadoutMode { get; set; }
    IReadOnlyList<string> ReadoutModes { get; }
    bool CanFastReadout { get; }
    bool FastReadout { get; set; }

    // Cooling
    bool CanSetCcdTemperature { get; }
    bool CanGetCoolerPower { get; }
    double CcdTemperature { get; }
    double SetCcdTemperature { get; set; }
    double CoolerPower { get; }
    double HeatSinkTemperature { get; }
    bool CoolerOn { get; set; }
}

/// <summary>Creates driver instances. Called on the dispatcher thread that will own the instance.</summary>
public interface IAscomDriverFactory
{
    IAscomFocuserDriver CreateFocuser(string progId);

    IAscomMountDriver CreateMount(string progId);

    IAscomCameraDriver CreateCamera(string progId);
}
