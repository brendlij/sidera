using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Tests.Rigs;

public class RigModelTests
{
    private static OpticalTrain ValidOptics() => new(750, 150, 3.76, 3.76, 6248, 4176);

    // RigId

    [Fact]
    public void RigId_AcceptsValidValue()
    {
        var id = new RigId("rig.main");

        Assert.Equal("rig.main", id.Value);
        Assert.Equal("rig.main", id.ToString());
    }

    [Fact]
    public void RigId_TrimsWhitespace()
    {
        Assert.Equal("rig.main", new RigId("  rig.main \t").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RigId_RejectsEmptyOrWhitespace(string? value)
    {
        Assert.Throws<ArgumentException>(() => new RigId(value!));
    }

    [Fact]
    public void RigId_HasValueEquality()
    {
        Assert.Equal(new RigId("rig.main"), new RigId(" rig.main "));
        Assert.NotEqual(new RigId("rig.main"), new RigId("rig.wide"));
        Assert.Equal(new RigId("rig.main").GetHashCode(), new RigId("rig.main").GetHashCode());
    }

    // OpticalTrain

    [Fact]
    public void OpticalTrain_AcceptsValidValues()
    {
        var optics = ValidOptics();

        Assert.Equal(750, optics.FocalLengthMm);
        Assert.Equal(150, optics.ApertureMm);
        Assert.Equal(5.0, optics.FocalRatio);
        Assert.Equal(3.76, optics.PixelSizeXMicrons);
        Assert.Equal(3.76, optics.PixelSizeYMicrons);
        Assert.Equal(6248, optics.SensorWidthPixels);
        Assert.Equal(4176, optics.SensorHeightPixels);
    }

    [Fact]
    public void OpticalTrain_OnlyTheFocalLengthIsRequired_AndTheRestIsUnknownNotZero()
    {
        var optics = new OpticalTrain(500);

        Assert.Null(optics.ApertureMm);
        Assert.Null(optics.FocalRatio);
        Assert.Null(optics.PixelSizeXMicrons);
        Assert.Null(optics.SensorWidthPixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void OpticalTrain_RejectsInvalidFocalLength(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpticalTrain(value, 150, 3.76, 3.76, 6248, 4176));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-150)]
    public void OpticalTrain_RejectsInvalidAperture(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpticalTrain(750, value, 3.76, 3.76, 6248, 4176));
    }

    [Theory]
    [InlineData(0, 3.76)]
    [InlineData(-3.76, 3.76)]
    [InlineData(3.76, 0)]
    [InlineData(3.76, -3.76)]
    public void OpticalTrain_RejectsInvalidPixelSize(double x, double y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpticalTrain(750, 150, x, y, 6248, 4176));
    }

    [Theory]
    [InlineData(0, 4176)]
    [InlineData(6248, 0)]
    [InlineData(-6248, 4176)]
    [InlineData(6248, -4176)]
    public void OpticalTrain_RejectsInvalidSensorPixels(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpticalTrain(750, 150, 3.76, 3.76, width, height));
    }

    // Rig

    [Fact]
    public void Rig_AcceptsFullDescription_AndTrimsName()
    {
        var optics = ValidOptics();

        var rig = new Rig(
            new RigId("rig.main"), "  Main Rig ", new DeviceId("camera.main"), optics,
            new DeviceId("focuser.main"), new DeviceId("wheel.main"));

        Assert.Equal(new RigId("rig.main"), rig.Id);
        Assert.Equal("Main Rig", rig.Name);
        Assert.Equal(new DeviceId("camera.main"), rig.CameraId);
        Assert.Equal(new DeviceId("focuser.main"), rig.FocuserId);
        Assert.Equal(new DeviceId("wheel.main"), rig.FilterWheelId);
        Assert.Same(optics, rig.Optics);
    }

    [Fact]
    public void Rig_AllowsOptionalDevicesToBeMissing()
    {
        var rig = new Rig(new RigId("rig.wide"), "Wide", new DeviceId("camera.wide"), ValidOptics());

        Assert.Null(rig.FocuserId);
        Assert.Null(rig.FilterWheelId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Rig_RejectsEmptyName(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new Rig(new RigId("rig.main"), name!, new DeviceId("camera.main"), ValidOptics()));
    }

    [Fact]
    public void Rig_MayHaveNoOptics_AndGetsThemLater()
    {
        var bare = new Rig(new RigId("rig.main"), "Main", new DeviceId("camera.main"));

        Assert.Null(bare.Optics);
        var with = bare.WithOptics(new OpticalTrain(750));
        Assert.Equal(750, with.Optics!.FocalLengthMm);
        Assert.Equal(bare.Id, with.Id);
        Assert.Equal(bare.CameraId, with.CameraId);
        Assert.Null(with.WithOptics(null).Optics);
    }
}
