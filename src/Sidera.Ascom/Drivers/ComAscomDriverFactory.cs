using ASCOM.Com.DriverAccess;
using ASCOM.Common.DeviceInterfaces;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using SideraAlignmentKind = Sidera.Core.Mounts.AlignmentKind;
using SideraAxisRate = Sidera.Core.Mounts.AxisRate;
using SideraGuideDirection = Sidera.Core.Mounts.GuideDirection;
using SideraMountAxis = Sidera.Core.Mounts.MountAxis;
using SideraPierSide = Sidera.Core.Mounts.PierSide;
using SideraTrackingRate = Sidera.Core.Mounts.TrackingRate;

namespace Sidera.Ascom.Drivers;

/// <summary>
/// The real drivers, through the DriverAccess classes of ASCOM.Com.Components. Each wrapper owns the DriverAccess
/// object it was created with and disposes it, which releases the COM object that DriverAccess created; nothing else is
/// ever released. The enumerations of ASCOM are translated here into those of Sidera, by their documented meaning, so that
/// no ASCOM type leaves this class.
/// </summary>
public sealed class ComAscomDriverFactory : IAscomDriverFactory
{
    public IAscomFocuserDriver CreateFocuser(string progId) => new ComFocuser(new Focuser(progId));

    public IAscomMountDriver CreateMount(string progId) => new ComMount(new Telescope(progId));

    public IAscomCameraDriver CreateCamera(string progId) => new ComCamera(new Camera(progId));

    // What the driver says about itself; each member may be missing, which is no reason to fail.
    private static DriverMetadata Identity(ASCOMDevice device)
    {
        static string? Read(Func<string?> get)
        {
            try
            {
                return get();
            }
            catch
            {
                return null;
            }
        }

        int? version;
        try
        {
            version = device.InterfaceVersion;
        }
        catch
        {
            version = null;
        }

        return new DriverMetadata(
            Read(() => device.Name), Read(() => device.Description), Read(() => device.DriverInfo), Read(() => device.DriverVersion), version);
    }

    private sealed class ComFocuser(Focuser inner) : IAscomFocuserDriver
    {
        public bool Connected
        {
            get => inner.Connected;
            set => inner.Connected = value;
        }

        public DriverMetadata Identity => ComAscomDriverFactory.Identity(inner);
        public bool Absolute => inner.Absolute;
        public int MaxStep => inner.MaxStep;
        public int MaxIncrement => inner.MaxIncrement;
        public double StepSize => inner.StepSize;
        public int Position => inner.Position;
        public bool IsMoving => inner.IsMoving;
        public double Temperature => inner.Temperature;
        public bool TempCompAvailable => inner.TempCompAvailable;

        public bool TempComp
        {
            get => inner.TempComp;
            set => inner.TempComp = value;
        }

        public void Move(int positionOrSteps) => inner.Move(positionOrSteps);

        public void Halt() => inner.Halt();

        public void SetupDialog() => inner.SetupDialog();

        public void Dispose() => inner.Dispose();
    }

    private sealed class ComMount(Telescope inner) : IAscomMountDriver
    {
        public bool Connected
        {
            get => inner.Connected;
            set => inner.Connected = value;
        }

        public DriverMetadata Identity => ComAscomDriverFactory.Identity(inner);

        public bool CanSlew => inner.CanSlew;
        public bool CanSlewAsync => inner.CanSlewAsync;
        public bool CanSlewAltAz => inner.CanSlewAltAz;
        public bool CanSlewAltAzAsync => inner.CanSlewAltAzAsync;
        public bool CanSync => inner.CanSync;
        public bool CanSyncAltAz => inner.CanSyncAltAz;
        public bool CanPark => inner.CanPark;
        public bool CanUnpark => inner.CanUnpark;
        public bool CanSetPark => inner.CanSetPark;
        public bool CanFindHome => inner.CanFindHome;
        public bool CanSetTracking => inner.CanSetTracking;
        public bool CanSetPierSide => inner.CanSetPierSide;
        public bool CanPulseGuide => inner.CanPulseGuide;
        public bool CanSetGuideRates => inner.CanSetGuideRates;
        public bool CanSetRightAscensionRate => inner.CanSetRightAscensionRate;
        public bool CanSetDeclinationRate => inner.CanSetDeclinationRate;

        public bool CanMoveAxis(SideraMountAxis axis) => inner.CanMoveAxis(ToAscom(axis));

        public IReadOnlyList<SideraAxisRate> AxisRates(SideraMountAxis axis)
        {
            var rates = inner.AxisRates(ToAscom(axis));
            try
            {
                var list = new List<SideraAxisRate>();
                foreach (IRate r in rates)
                {
                    list.Add(new SideraAxisRate(r.Minimum, r.Maximum));
                }

                return list;
            }
            finally
            {
                rates.Dispose();
            }
        }

        public bool AtPark => inner.AtPark;
        public bool AtHome => inner.AtHome;
        public bool Slewing => inner.Slewing;
        public bool IsPulseGuiding => inner.IsPulseGuiding;

        public bool Tracking
        {
            get => inner.Tracking;
            set => inner.Tracking = value;
        }

        public double RightAscension => inner.RightAscension;
        public double Declination => inner.Declination;
        public double Altitude => inner.Altitude;
        public double Azimuth => inner.Azimuth;

        public SideraTrackingRate TrackingRate
        {
            get => FromAscom(inner.TrackingRate);
            set => inner.TrackingRate = ToAscom(value);
        }

        public IReadOnlyList<SideraTrackingRate> TrackingRates
        {
            get
            {
                var rates = inner.TrackingRates;
                try
                {
                    var list = new List<SideraTrackingRate>();
                    foreach (DriveRate r in rates)
                    {
                        list.Add(FromAscom(r));
                    }

                    return list;
                }
                finally
                {
                    rates.Dispose();
                }
            }
        }

        public SideraPierSide SideOfPier => FromAscom(inner.SideOfPier);

        public SideraPierSide DestinationSideOfPier(double rightAscensionHours, double declinationDegrees) =>
            FromAscom(inner.DestinationSideOfPier(rightAscensionHours, declinationDegrees));

        public double SiderealTime => inner.SiderealTime;
        public DateTime UtcDate => inner.UTCDate;

        public double GuideRateRightAscension
        {
            get => inner.GuideRateRightAscension;
            set => inner.GuideRateRightAscension = value;
        }

        public double GuideRateDeclination
        {
            get => inner.GuideRateDeclination;
            set => inner.GuideRateDeclination = value;
        }

        public bool DoesRefraction
        {
            get => inner.DoesRefraction;
            set => inner.DoesRefraction = value;
        }

        public EquatorialSystemKind EquatorialSystem => inner.EquatorialSystem switch
        {
            EquatorialCoordinateType.Topocentric => EquatorialSystemKind.Topocentric,
            EquatorialCoordinateType.J2000 => EquatorialSystemKind.J2000,
            EquatorialCoordinateType.J2050 => EquatorialSystemKind.J2050,
            EquatorialCoordinateType.B1950 => EquatorialSystemKind.B1950,
            _ => EquatorialSystemKind.Other,
        };

        public SideraAlignmentKind AlignmentMode => inner.AlignmentMode switch
        {
            ASCOM.Common.DeviceInterfaces.AlignmentMode.AltAz => SideraAlignmentKind.AltAz,
            ASCOM.Common.DeviceInterfaces.AlignmentMode.Polar => SideraAlignmentKind.Polar,
            _ => SideraAlignmentKind.GermanPolar,
        };

        public double ApertureArea => inner.ApertureArea;
        public double ApertureDiameter => inner.ApertureDiameter;
        public double FocalLength => inner.FocalLength;
        public double SiteLatitude
        {
            get => inner.SiteLatitude;
            set => inner.SiteLatitude = value;
        }

        public double SiteLongitude
        {
            get => inner.SiteLongitude;
            set => inner.SiteLongitude = value;
        }

        public double SiteElevation
        {
            get => inner.SiteElevation;
            set => inner.SiteElevation = value;
        }


        public void SlewToCoordinates(double rightAscensionHours, double declinationDegrees) =>
            inner.SlewToCoordinates(rightAscensionHours, declinationDegrees);

        public void SlewToCoordinatesAsync(double rightAscensionHours, double declinationDegrees) =>
            inner.SlewToCoordinatesAsync(rightAscensionHours, declinationDegrees);

        public void SlewToAltAz(double altitudeDegrees, double azimuthDegrees) => inner.SlewToAltAz(altitudeDegrees, azimuthDegrees);

        public void SlewToAltAzAsync(double altitudeDegrees, double azimuthDegrees) => inner.SlewToAltAzAsync(altitudeDegrees, azimuthDegrees);

        public void SyncToCoordinates(double rightAscensionHours, double declinationDegrees) =>
            inner.SyncToCoordinates(rightAscensionHours, declinationDegrees);

        public void AbortSlew() => inner.AbortSlew();

        public void Park() => inner.Park();

        public void Unpark() => inner.Unpark();

        public void SetPark() => inner.SetPark();

        public void FindHome() => inner.FindHome();

        public void PulseGuide(SideraGuideDirection direction, int durationMilliseconds) =>
            inner.PulseGuide(
                direction switch
                {
                    SideraGuideDirection.North => ASCOM.Common.DeviceInterfaces.GuideDirection.North,
                    SideraGuideDirection.South => ASCOM.Common.DeviceInterfaces.GuideDirection.South,
                    SideraGuideDirection.East => ASCOM.Common.DeviceInterfaces.GuideDirection.East,
                    _ => ASCOM.Common.DeviceInterfaces.GuideDirection.West,
                },
                durationMilliseconds);

        public void MoveAxis(SideraMountAxis axis, double degreesPerSecond) => inner.MoveAxis(ToAscom(axis), degreesPerSecond);

        public void SetupDialog() => inner.SetupDialog();

        public void Dispose() => inner.Dispose();

        private static TelescopeAxis ToAscom(SideraMountAxis axis) => axis switch
        {
            SideraMountAxis.Primary => TelescopeAxis.Primary,
            SideraMountAxis.Secondary => TelescopeAxis.Secondary,
            _ => TelescopeAxis.Tertiary,
        };

        private static DriveRate ToAscom(SideraTrackingRate rate) => rate switch
        {
            SideraTrackingRate.Lunar => DriveRate.Lunar,
            SideraTrackingRate.Solar => DriveRate.Solar,
            SideraTrackingRate.King => DriveRate.King,
            _ => DriveRate.Sidereal,
        };

        private static SideraTrackingRate FromAscom(DriveRate rate) => rate switch
        {
            DriveRate.Lunar => SideraTrackingRate.Lunar,
            DriveRate.Solar => SideraTrackingRate.Solar,
            DriveRate.King => SideraTrackingRate.King,
            _ => SideraTrackingRate.Sidereal,
        };

        // ASCOM names the two states of a German mount "Normal" (the telescope is on the east side of the pier, looking
        // west) and "ThroughThePole" (west side, looking east); this is the meaning of pierEast = 0 and pierWest = 1.
        private static SideraPierSide FromAscom(PointingState side) => side switch
        {
            PointingState.Normal => SideraPierSide.East,
            PointingState.ThroughThePole => SideraPierSide.West,
            _ => SideraPierSide.Unknown,
        };
    }

    private sealed class ComCamera(Camera inner) : IAscomCameraDriver
    {
        public bool Connected
        {
            get => inner.Connected;
            set => inner.Connected = value;
        }

        public DriverMetadata Identity => ComAscomDriverFactory.Identity(inner);

        public int CameraXSize => inner.CameraXSize;
        public int CameraYSize => inner.CameraYSize;
        public int MaxAdu => inner.MaxADU;
        public double PixelSizeX => inner.PixelSizeX;
        public double PixelSizeY => inner.PixelSizeY;
        public double ElectronsPerAdu => inner.ElectronsPerADU;
        public double FullWellCapacity => inner.FullWellCapacity;

        public SensorKind SensorType => inner.SensorType switch
        {
            ASCOM.Common.DeviceInterfaces.SensorType.Monochrome => SensorKind.Monochrome,
            ASCOM.Common.DeviceInterfaces.SensorType.Color => SensorKind.Color,
            ASCOM.Common.DeviceInterfaces.SensorType.RGGB => SensorKind.Rggb,
            ASCOM.Common.DeviceInterfaces.SensorType.CMYG => SensorKind.Cmyg,
            ASCOM.Common.DeviceInterfaces.SensorType.CMYG2 => SensorKind.Cmyg2,
            ASCOM.Common.DeviceInterfaces.SensorType.LRGB => SensorKind.Lrgb,
            _ => SensorKind.Unknown,
        };

        public string SensorName => inner.SensorName;
        public int BayerOffsetX => inner.BayerOffsetX;
        public int BayerOffsetY => inner.BayerOffsetY;
        public bool HasShutter => inner.HasShutter;

        public int NumX
        {
            get => inner.NumX;
            set => inner.NumX = value;
        }

        public int NumY
        {
            get => inner.NumY;
            set => inner.NumY = value;
        }

        public int StartX
        {
            get => inner.StartX;
            set => inner.StartX = value;
        }

        public int StartY
        {
            get => inner.StartY;
            set => inner.StartY = value;
        }

        public int BinX
        {
            get => inner.BinX;
            set => inner.BinX = checked((short)value);
        }

        public int BinY
        {
            get => inner.BinY;
            set => inner.BinY = checked((short)value);
        }

        public int MaxBinX => inner.MaxBinX;
        public int MaxBinY => inner.MaxBinY;
        public bool CanAsymmetricBin => inner.CanAsymmetricBin;
        public bool CanAbortExposure => inner.CanAbortExposure;
        public bool CanStopExposure => inner.CanStopExposure;
        public double ExposureMin => inner.ExposureMin;
        public double ExposureMax => inner.ExposureMax;
        public double ExposureResolution => inner.ExposureResolution;
        public AscomCameraState CameraState => (AscomCameraState)(int)inner.CameraState;
        public bool ImageReady => inner.ImageReady;
        public double LastExposureDuration => inner.LastExposureDuration;
        public string LastExposureStartTime => inner.LastExposureStartTime;

        public void StartExposure(double durationSeconds, bool light) => inner.StartExposure(durationSeconds, light);

        public void AbortExposure() => inner.AbortExposure();

        public void StopExposure() => inner.StopExposure();

        public object? ImageArray => inner.ImageArray;

        public int Gain
        {
            get => inner.Gain;
            set => inner.Gain = checked((short)value);
        }

        public int GainMin => inner.GainMin;
        public int GainMax => inner.GainMax;
        public IReadOnlyList<string> Gains => [.. inner.Gains];

        public int Offset
        {
            get => inner.Offset;
            set => inner.Offset = value;
        }

        public int OffsetMin => inner.OffsetMin;
        public int OffsetMax => inner.OffsetMax;
        public IReadOnlyList<string> Offsets => [.. inner.Offsets];

        public int ReadoutMode
        {
            get => inner.ReadoutMode;
            set => inner.ReadoutMode = checked((short)value);
        }

        public IReadOnlyList<string> ReadoutModes => [.. inner.ReadoutModes];
        public bool CanFastReadout => inner.CanFastReadout;

        public bool FastReadout
        {
            get => inner.FastReadout;
            set => inner.FastReadout = value;
        }

        public bool CanSetCcdTemperature => inner.CanSetCCDTemperature;
        public bool CanGetCoolerPower => inner.CanGetCoolerPower;
        public double CcdTemperature => inner.CCDTemperature;

        public double SetCcdTemperature
        {
            get => inner.SetCCDTemperature;
            set => inner.SetCCDTemperature = value;
        }

        public double CoolerPower => inner.CoolerPower;
        public double HeatSinkTemperature => inner.HeatSinkTemperature;

        public bool CoolerOn
        {
            get => inner.CoolerOn;
            set => inner.CoolerOn = value;
        }

        public void SetupDialog() => inner.SetupDialog();

        public void Dispose() => inner.Dispose();
    }
}
