using Sidera.Core.Astrometry;
using Sidera.Core.Cameras;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Tests.Rigs;

/// <summary>The camera database, how a camera is found in it, and how its geometry is resolved with the device's and the user's: manual, device, database, unknown.</summary>
public sealed class CameraDatabaseTests
{
    private static CameraDatabaseEntry Entry(string model, string[]? aliases = null, int width = 6248, int height = 4176, double pixel = 3.76, string manufacturer = "ZWO") =>
        new(manufacturer, model, aliases ?? [], "Test Sensor", width, height, pixel, pixel);

    // ---- The embedded database

    [Fact]
    public void TheEmbeddedDatabase_IsAVersionedResource_WithValidEntries()
    {
        var db = CameraDatabase.Default;

        Assert.Equal(1, db.Version);
        Assert.NotEmpty(db.Entries);
        Assert.All(db.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Manufacturer));
            Assert.False(string.IsNullOrWhiteSpace(e.Model));
            Assert.InRange(e.WidthPixels, 100, 20000);
            Assert.InRange(e.HeightPixels, 100, 20000);
            Assert.InRange(e.PixelSizeXMicrometers, 1, 20);
            Assert.InRange(e.PixelSizeYMicrometers, 1, 20);
        });
    }

    [Fact]
    public void TheEmbeddedDatabase_HasTheTestedCamera_WithItsVerifiedGeometry()
    {
        var entry = CameraDatabase.Default.Find("ZWO ASI2600MC Pro")!;

        Assert.Equal("ZWO", entry.Manufacturer);
        Assert.Equal(6248, entry.WidthPixels);
        Assert.Equal(4176, entry.HeightPixels);
        Assert.Equal(3.76, entry.PixelSizeXMicrometers);
        Assert.Equal(3.76, entry.PixelSizeYMicrometers);
        Assert.Equal("Sony IMX571", entry.SensorName);
        Assert.Equal(CameraColorType.Bayer, entry.ColorType);
        Assert.Equal("RGGB", entry.BayerPattern);
        Assert.Equal(CameraColorType.Mono, CameraDatabase.Default.Find("ASI2600MM Pro")!.ColorType);
    }

    [Fact]
    public void TheEmbeddedDatabase_HasNoTwoEntriesThatAnyNameFits()
    {
        var keys = CameraDatabase.Default.Entries.SelectMany(e => new[] { e.Model }.Concat(e.Aliases).Select(n => (Key: CameraDatabase.Normalize(n), Entry: e)));

        Assert.All(keys.GroupBy(k => k.Key), g => Assert.Single(g.Select(k => k.Entry).Distinct()));
    }

    [Fact]
    public void ADatabaseThatIsNotOfAKnownFormatOrVersion_IsRefused()
    {
        Assert.Throws<CameraDatabaseException>(() => CameraDatabase.Parse("{\"format\":\"something\",\"version\":1,\"cameras\":[]}"));
        Assert.Throws<CameraDatabaseException>(() => CameraDatabase.Parse("{\"format\":\"sidera-camera-database\",\"version\":99,\"cameras\":[]}"));
        Assert.Throws<CameraDatabaseException>(() => CameraDatabase.Parse("not json"));
    }

    [Theory]
    [InlineData("{\"manufacturer\":\"X\",\"model\":\"Y\",\"widthPixels\":0,\"heightPixels\":10,\"pixelSizeXMicrometers\":3,\"pixelSizeYMicrometers\":3}")]
    [InlineData("{\"manufacturer\":\"X\",\"model\":\"Y\",\"widthPixels\":10,\"heightPixels\":10,\"pixelSizeXMicrometers\":-1,\"pixelSizeYMicrometers\":3}")]
    [InlineData("{\"manufacturer\":\"X\",\"model\":\"\",\"widthPixels\":10,\"heightPixels\":10,\"pixelSizeXMicrometers\":3,\"pixelSizeYMicrometers\":3}")]
    [InlineData("{\"manufacturer\":\"X\",\"model\":\"Y\",\"widthPixels\":10,\"heightPixels\":10,\"pixelSizeXMicrometers\":3,\"pixelSizeYMicrometers\":3,\"color\":\"Purple\"}")]
    public void AnEntryWithInvalidGeometry_IsRefused_NotKept(string entry) =>
        Assert.Throws<CameraDatabaseException>(() => CameraDatabase.Parse("{\"format\":\"sidera-camera-database\",\"version\":1,\"cameras\":[" + entry + "]}"));

    [Fact]
    public void AValidDatabaseTextRoundTripsIntoEntries()
    {
        var db = CameraDatabase.Parse(
            "{\"format\":\"sidera-camera-database\",\"version\":1,\"cameras\":[{\"manufacturer\":\"Acme\",\"model\":\"Cam 1\",\"aliases\":[\"C1\"],\"widthPixels\":100,\"heightPixels\":50,\"pixelSizeXMicrometers\":4,\"pixelSizeYMicrometers\":5,\"color\":\"mono\"}]}");

        var entry = Assert.Single(db.Entries);
        Assert.Equal(["C1"], entry.Aliases);
        Assert.Equal(CameraColorType.Mono, entry.ColorType);
        Assert.Equal(5, entry.PixelSizeYMicrometers);
        Assert.Same(entry, db.Find("acme cam1"));
    }

    // ---- Matching

    [Fact]
    public void AnExactModel_Matches()
    {
        var db = new CameraDatabase([Entry("ASI2600MC Pro")]);

        Assert.Equal("ASI2600MC Pro", db.Find("ASI2600MC Pro")!.Model);
    }

    [Theory]
    [InlineData("asi2600mc pro")]
    [InlineData("ASI2600MC PRO")]
    [InlineData("  ASI2600MC   Pro  ")]
    [InlineData("ZWO ASI2600MC Pro")]
    [InlineData("zwo   asi2600mc   pro")]
    [InlineData("ZWO ASI2600MC Pro (1)")]
    [InlineData("ASI-2600MC Pro")]
    [InlineData("ZWOptical ASI2600MC Pro")]
    public void CaseWhitespaceAPrefixAndABracketSuffix_DoNotMatter(string name)
    {
        var db = new CameraDatabase([Entry("ASI2600MC Pro")]);

        Assert.NotNull(db.Find(name));
    }

    [Fact]
    public void AnAlias_Matches()
    {
        var db = new CameraDatabase([Entry("ASI2600MC Pro", ["ASI2600MC", "ZWO ASI2600 Color"])]);

        Assert.NotNull(db.Find("ASI2600MC"));
        Assert.NotNull(db.Find("zwo asi2600 color"));
    }

    [Fact]
    public void ADriverNameThatDoesNotSayTheModel_ButTheDescriptionDoes_Matches()
    {
        var db = CameraDatabase.Default;

        Assert.Equal("ASI2600MC Pro", db.Find(["ZWO ASI Camera (1)", "My imaging camera", "ZWO ASI2600MC Pro"])!.Model);
    }

    [Theory]
    [InlineData("Main Camera")]
    [InlineData("ZWO ASI Camera (1)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ASI")]
    public void ANameThatIsNotInTheDatabase_FindsNothing(string name) => Assert.Null(CameraDatabase.Default.Find(name));

    [Fact]
    public void ASimilarModel_IsNotTakenForAKnownOne()
    {
        var db = CameraDatabase.Default;

        Assert.Null(db.Find("ASI2600MC Air")); // contains a known model, is not it
        Assert.Null(db.Find("ASI2600")); // a part of it
        Assert.Null(db.Find("ASI2601MC Pro"));
        Assert.NotEqual(db.Find("ASI2600MC Pro"), db.Find("ASI2600MM Pro")); // color and mono are different cameras
    }

    [Fact]
    public void ANameThatFitsMoreThanOneEntry_FindsNone()
    {
        var db = new CameraDatabase([Entry("ASI100 Pro", ["Shared"]), Entry("ASI200 Pro", ["Shared"], width: 100, height: 100)]);

        Assert.Null(db.Find("Shared"));
        Assert.NotNull(db.Find("ASI100 Pro"));
    }

    [Fact]
    public void NamesThatPointToDifferentEntries_FindNone_ButNamesOfOneEntryDo()
    {
        var db = new CameraDatabase([Entry("ASI100 Pro"), Entry("ASI200 Pro", width: 100, height: 100)]);

        Assert.Null(db.Find(["ASI100 Pro", "ASI200 Pro"]));
        Assert.NotNull(db.Find(["ASI100 Pro", "ZWO ASI100 Pro (2)", null]));
    }

    // ---- Resolution policy

    private static OpticalTrain Configured(double? px = null, double? py = null, int? w = null, int? h = null) => new(750, pixelSizeXMicrons: px, pixelSizeYMicrons: py, sensorWidthPixels: w, sensorHeightPixels: h);

    private static SensorGeometry Device(int? w = null, int? h = null, double? px = null, double? py = null) => new(w, h, px, py);

    private static readonly CameraDatabaseEntry Known = Entry("ASI2600MC Pro");

    [Fact]
    public void Manual_WinsOverTheDeviceAndTheDatabase()
    {
        var sensor = SensorGeometry.Combine(Device(6248, 4176, 3.76, 3.76), Known);

        var g = OpticalTrainGeometry.Resolve(Configured(px: 4.0, py: 4.1, w: 5000, h: 3000), sensor);

        Assert.Equal(4.0, g.PixelSizeXMicrons);
        Assert.Equal(4.1, g.PixelSizeYMicrons);
        Assert.Equal(5000, g.SensorWidthPixels);
        Assert.Equal(3000, g.SensorHeightPixels);
        Assert.Equal(GeometrySource.Configured, g.PixelSizeXSource);
        Assert.Equal(GeometrySource.Configured, g.SensorHeightSource);
    }

    [Fact]
    public void TheDatabase_NeverOverridesAManualValue_EvenWhenTheDeviceSaysNothing()
    {
        var sensor = SensorGeometry.Combine(null, Known);

        var g = OpticalTrainGeometry.Resolve(Configured(px: 9.0), sensor);

        Assert.Equal(9.0, g.PixelSizeXMicrons);
        Assert.Equal(GeometrySource.Configured, g.PixelSizeXSource);
        Assert.Equal(3.76, g.PixelSizeYMicrons); // the other field is still resolved on its own
        Assert.Equal(GeometrySource.Database, g.PixelSizeYSource);
    }

    [Fact]
    public void TheDevice_WinsOverTheDatabase()
    {
        var sensor = SensorGeometry.Combine(Device(6248, 4176, 3.80, 3.80), Known);

        var g = OpticalTrainGeometry.Resolve(Configured(), sensor);

        Assert.Equal(3.80, g.PixelSizeXMicrons);
        Assert.Equal(GeometrySource.DeviceReported, g.PixelSizeXSource);
        Assert.Equal(GeometrySource.DeviceReported, g.PixelSizeSource);
    }

    [Fact]
    public void TheDatabase_FillsAMissingValue_AndEachFieldIsResolvedAlone()
    {
        // The driver knows the pixel counts and not the pixel size.
        var sensor = SensorGeometry.Combine(Device(6248, 4176), Known);

        var g = OpticalTrainGeometry.Resolve(Configured(), sensor);

        Assert.Equal(6248, g.SensorWidthPixels);
        Assert.Equal(GeometrySource.DeviceReported, g.SensorWidthSource);
        Assert.Equal(GeometrySource.DeviceReported, g.SensorPixelsSource);
        Assert.Equal(3.76, g.PixelSizeXMicrons);
        Assert.Equal(GeometrySource.Database, g.PixelSizeXSource);
        Assert.Equal(GeometrySource.Database, g.PixelSizeSource);
        Assert.Empty(g.Conflicts);
    }

    [Fact]
    public void ADeviceThatReportsOneAxisOnly_GetsTheOtherFromTheDatabase()
    {
        var sensor = SensorGeometry.Combine(Device(w: 6248, px: 3.76), Known);

        Assert.Equal(GeometrySource.DeviceReported, sensor!.WidthSource);
        Assert.Equal(GeometrySource.Database, sensor.HeightSource);
        Assert.Equal(4176, sensor.HeightPixels);
        Assert.Equal(GeometrySource.DeviceReported, sensor.PixelSizeXSource);
        Assert.Equal(GeometrySource.Database, sensor.PixelSizeYSource);
    }

    [Fact]
    public void WithoutADevice_AKnownCameraStillHasItsGeometry_FromTheDatabase()
    {
        var g = OpticalTrainGeometry.Resolve(Configured(), SensorGeometry.Combine(null, Known));

        Assert.Equal(6248, g.SensorWidthPixels);
        Assert.Equal(GeometrySource.Database, g.SensorWidthSource);
        Assert.Equal(GeometrySource.Database, g.PixelSizeSource);
        Assert.Equal(Known, g.DatabaseEntry);
    }

    [Fact]
    public void Unknown_RemainsUnknown_WithoutAnySource()
    {
        var g = OpticalTrainGeometry.Resolve(Configured(), SensorGeometry.Combine(null, null));

        Assert.Null(g.PixelSizeXMicrons);
        Assert.Null(g.SensorWidthPixels);
        Assert.Null(g.PixelScaleXArcsecPerPixel);
        Assert.Null(g.FieldOfViewXDegrees);
        Assert.Equal(GeometrySource.Unknown, g.PixelSizeSource);
        Assert.Equal(GeometrySource.Unknown, g.SensorPixelsSource);
        Assert.Null(g.DatabaseEntry);
    }

    [Fact]
    public void ADeviceWithoutAMatch_ResolvesAsBefore()
    {
        var device = Device(100, 50, 3.0, 3.0);

        Assert.Same(device, SensorGeometry.Combine(device, null));
    }

    [Fact]
    public void NothingOfTheDatabase_IsCopiedIntoTheRigConfiguration()
    {
        var optics = Configured(px: 4.0);

        _ = OpticalTrainGeometry.Resolve(optics, SensorGeometry.Combine(null, Known));

        Assert.Null(optics.PixelSizeYMicrons);
        Assert.Null(optics.SensorWidthPixels);
        Assert.Equal(4.0, optics.PixelSizeXMicrons);
    }

    // ---- Conflicts

    [Theory]
    [InlineData(3.76, 3.76)]
    [InlineData(3.7601, 3.76)]
    [InlineData(3.77, 3.76)]
    public void EquivalentValues_AreNoConflict(double reported, double database)
    {
        var sensor = SensorGeometry.Combine(Device(6248, 4176, reported, reported), Entry("ASI2600MC Pro", pixel: database));

        Assert.Empty(sensor!.Conflicts);
    }

    [Fact]
    public void AMeaningfulPixelSizeMismatch_IsReported_AndNeitherValueIsReplaced()
    {
        var sensor = SensorGeometry.Combine(Device(6248, 4176, 3.80, 3.80), Known)!;

        var conflict = Assert.Single(sensor.Conflicts, c => c.Field == GeometryField.PixelSizeXMicrons);
        Assert.Equal(3.80, conflict.DeviceValue);
        Assert.Equal(3.76, conflict.DatabaseValue);
        Assert.Equal(2, sensor.Conflicts.Count(c => c.IsPixelSize)); // both axes
        Assert.Equal(3.80, sensor.PixelSizeXMicrons); // the device's value is used
        Assert.Equal(3.76, Known.PixelSizeXMicrometers); // the database is unchanged
    }

    [Fact]
    public void AMeaningfulResolutionMismatch_IsReported()
    {
        var sensor = SensorGeometry.Combine(Device(9576, 6388, 3.76, 3.76), Known)!;

        Assert.Equal([GeometryField.WidthPixels, GeometryField.HeightPixels], sensor.Conflicts.Select(c => c.Field));
        Assert.Equal(9576, sensor.WidthPixels);
        Assert.Equal(6248, sensor.Conflicts[0].DatabaseValue);
        Assert.Equal(GeometrySource.DeviceReported, sensor.WidthSource);
    }

    [Fact]
    public void AConflict_ReachesTheResolvedGeometry_AndDoesNotStopTheCamera()
    {
        var sensor = SensorGeometry.Combine(Device(9576, 6388, 3.80, 3.80), Known);

        var g = OpticalTrainGeometry.Resolve(Configured(), sensor);

        Assert.Equal(4, g.Conflicts.Count);
        Assert.NotNull(g.PixelScaleXArcsecPerPixel);
        Assert.NotNull(g.FieldOfViewXDegrees);
    }

    // ---- The device and the integration

    [Fact]
    public async Task ForADevice_TheNameOfTheCameraIsWhatFindsItInTheDatabase()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera.known"), "ZWO ASI2600MC Pro", 1);
        var other = host.AddSimulatedCamera(new("camera.other"), "Main Camera", 1);

        var known = SensorGeometry.For(camera)!;
        var unknown = SensorGeometry.For(other);

        Assert.Equal("ASI2600MC Pro", known.DatabaseEntry!.Model);
        Assert.Equal(GeometrySource.Database, known.WidthSource); // not connected: nothing is reported
        Assert.Equal(6248, known.WidthPixels);
        Assert.Null(unknown); // not connected and not known: nothing
    }

    [Fact]
    public async Task ForAConnectedKnownCamera_TheDeviceIsPreferred_AndADifferenceIsAConflict()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera.known"), "ZWO ASI2600MC Pro", 1);
        await camera.ConnectAsync();

        var sensor = SensorGeometry.For(camera)!;

        Assert.Equal(GeometrySource.DeviceReported, sensor.WidthSource);
        Assert.Equal(GeometrySource.DeviceReported, sensor.PixelSizeXSource);
        Assert.NotEqual(6248, sensor.WidthPixels); // the simulator's own sensor
        Assert.Contains(sensor.Conflicts, c => c.Field == GeometryField.WidthPixels);
        Assert.DoesNotContain(sensor.Conflicts, c => c.IsPixelSize); // 3.76 both
    }

    [Fact]
    public void AFieldOfView_AndAPixelScale_AreComputedFromTheDatabaseValues()
    {
        var g = OpticalTrainGeometry.Resolve(new OpticalTrain(750), SensorGeometry.Combine(null, Known));

        Assert.Equal(206.264806247 * 3.76 / 750, g.PixelScaleXArcsecPerPixel!.Value, 9);
        Assert.Equal(1.034, g.PixelScaleXArcsecPerPixel.Value, 3);
        var widthMm = 3.76 * 6248 / 1000;
        Assert.Equal(2 * Math.Atan(widthMm / (2 * 750)) * 180 / Math.PI, g.FieldOfViewXDegrees!.Value, 9);
        Assert.Equal(1.79, g.FieldOfViewXDegrees.Value, 2);
        Assert.Equal(1.20, g.FieldOfViewYDegrees!.Value, 2);
    }

    [Fact]
    public async Task ThePlateSolveHints_ReceiveTheDatabaseGeometry_OfAKnownCamera()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera.known"), "ZWO ASI2600MC Pro", 1);
        var rig = new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(750));

        // A camera that is not connected: the hints come from the database, through the same resolver as before.
        var sensor = SensorGeometry.For(camera);
        var frame = new CameraFrame(1000, 800, new ushort[800000], TimeSpan.Zero);
        var request = PlateSolveHintResolver.Resolve(frame, rig, sensor, null, new PlateSolveDefaults());

        Assert.Equal(1.034, request.PixelScaleXArcsecPerPixel!.Value, 3);
        // The field is that of the frame that was taken: 1000 pixels of 3.76 µm behind 750 mm.
        Assert.Equal(OpticalTrainGeometry.FieldOfViewDegrees(3.76 * 1000 / 1000, 750), request.FieldOfViewXDegrees!.Value, 9);
        Assert.Equal(3.76, request.Image.PixelSizeXMicrons);
        Assert.Equal(750, request.FocalLengthMm);
    }

    [Fact]
    public async Task ThePlateSolveService_ResolvesTheSameWay_AndAnExplicitOverrideStillWins()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera.known"), "ZWO ASI2600MC Pro", 1);
        host.ConfigurePlateSolver(new NoSolver());
        var frame = new CameraFrame(1000, 800, new ushort[800000], TimeSpan.Zero);

        var fromDatabase = host.PlateSolving!.Resolve(frame, new Rig(new("rig.a"), "A", camera.Id, new OpticalTrain(750)), null, new PlateSolveDefaults());
        var manual = host.PlateSolving.Resolve(frame, new Rig(new("rig.b"), "B", camera.Id, new OpticalTrain(750, pixelSizeXMicrons: 5.0, pixelSizeYMicrons: 5.0)), null, new PlateSolveDefaults());

        Assert.Equal(1.034, fromDatabase.PixelScaleXArcsecPerPixel!.Value, 3);
        Assert.Equal(206.264806247 * 5.0 / 750, manual.PixelScaleXArcsecPerPixel!.Value, 9);
    }

    private sealed class NoSolver : IPlateSolver
    {
        public string Name => "None";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));
        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
