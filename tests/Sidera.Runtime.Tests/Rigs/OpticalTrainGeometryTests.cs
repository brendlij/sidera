using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Tests.Rigs;

public sealed class OpticalTrainGeometryTests
{
    [Fact]
    public void ThePixelScale_IsTheRadianInArcsecondsTimesPixelSizeOverFocalLength()
    {
        var g = OpticalTrainGeometry.Compute(750, 3.76, 3.76, 6248, 4176);

        var expected = 206.264806247 * 3.76 / 750;
        Assert.Equal(expected, g.PixelScaleXArcsecPerPixel!.Value, 9);
        Assert.Equal(expected, g.PixelScaleYArcsecPerPixel!.Value, 9);
        Assert.Equal(1.034, g.PixelScaleXArcsecPerPixel.Value, 3);
    }

    [Fact]
    public void PixelsThatAreNotSquare_GiveAScaleForEachAxis()
    {
        var g = OpticalTrainGeometry.Compute(1000, 3.0, 6.0, 1000, 500);

        Assert.Equal(0.6188, g.PixelScaleXArcsecPerPixel!.Value, 4);
        Assert.Equal(1.2376, g.PixelScaleYArcsecPerPixel!.Value, 4);
        Assert.Equal(3.0, g.SensorWidthMm!.Value, 9);
        Assert.Equal(3.0, g.SensorHeightMm!.Value, 9);
    }

    [Fact]
    public void TheSensorSize_IsPixelSizeTimesPixelsInMillimeters()
    {
        var g = OpticalTrainGeometry.Compute(750, 3.76, 3.76, 6248, 4176);

        Assert.Equal(3.76 * 6248 / 1000, g.SensorWidthMm!.Value, 9);
        Assert.Equal(3.76 * 4176 / 1000, g.SensorHeightMm!.Value, 9);
    }

    [Fact]
    public void TheFieldOfView_IsTwiceTheArcTangentOfHalfTheSensorOverTheFocalLength()
    {
        var g = OpticalTrainGeometry.Compute(750, 3.76, 3.76, 6248, 4176);

        var width = 3.76 * 6248 / 1000;
        var height = 3.76 * 4176 / 1000;
        Assert.Equal(2 * Math.Atan(width / 1500) * 180 / Math.PI, g.FieldOfViewXDegrees!.Value, 9);
        Assert.Equal(2 * Math.Atan(height / 1500) * 180 / Math.PI, g.FieldOfViewYDegrees!.Value, 9);
        Assert.Equal(2 * Math.Atan(Math.Sqrt(width * width + height * height) / 1500) * 180 / Math.PI, g.DiagonalFieldOfViewDegrees!.Value, 9);
        Assert.Equal(1.79, g.FieldOfViewXDegrees.Value, 2);
        Assert.Equal(1.20, g.FieldOfViewYDegrees.Value, 2);
    }

    [Fact]
    public void AWideFieldIsNotTheSmallAngleApproximation()
    {
        // 36 mm behind 18 mm: exactly 90 degrees, where the small-angle formula would say 114.6.
        var g = OpticalTrainGeometry.Compute(18, 36, 36, 1000, 1000);

        Assert.Equal(90, g.FieldOfViewXDegrees!.Value, 9);
        Assert.True(OpticalTrainGeometry.FieldOfViewDegrees(36, 18) < 114);
    }

    [Fact]
    public void WithoutAFocalLength_NothingIsDerived_AndNothingIsZero()
    {
        var g = OpticalTrainGeometry.Compute(null, 3.76, 3.76, 6248, 4176);

        Assert.Null(g.PixelScaleXArcsecPerPixel);
        Assert.Null(g.FieldOfViewXDegrees);
        Assert.Null(g.DiagonalFieldOfViewDegrees);
        Assert.NotNull(g.SensorWidthMm); // the sensor does not need a telescope
    }

    [Fact]
    public void WithoutSensorInformation_ThePixelScaleStillComesWithAPixelSize_ButNotTheFieldOfView()
    {
        var g = OpticalTrainGeometry.Compute(750, 3.76, 3.76, null, null);

        Assert.NotNull(g.PixelScaleXArcsecPerPixel);
        Assert.Null(g.SensorWidthMm);
        Assert.Null(g.FieldOfViewXDegrees);

        var none = OpticalTrainGeometry.Compute(750, null, null, null, null);
        Assert.Null(none.PixelScaleXArcsecPerPixel);
        Assert.Null(none.PixelScaleYArcsecPerPixel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void ANumberThatIsNotAPositiveLength_CountsAsUnknown(double value)
    {
        Assert.Null(OpticalTrainGeometry.Compute(value, 3.76, 3.76, 100, 100).PixelScaleXArcsecPerPixel);
        Assert.Null(OpticalTrainGeometry.Compute(750, value, value, 100, 100).PixelScaleXArcsecPerPixel);
    }

    [Fact]
    public void ChangingTheFocalLength_ChangesEveryDerivedValue()
    {
        var before = OpticalTrainGeometry.Resolve(new OpticalTrain(750, null, 3.76, 3.76, 6248, 4176), null);
        var after = OpticalTrainGeometry.Resolve(new OpticalTrain(1500, null, 3.76, 3.76, 6248, 4176), null);

        Assert.Equal(before.PixelScaleXArcsecPerPixel!.Value / 2, after.PixelScaleXArcsecPerPixel!.Value, 9);
        Assert.True(after.FieldOfViewXDegrees < before.FieldOfViewXDegrees);
        Assert.Equal(before.SensorWidthMm, after.SensorWidthMm);
    }

    // ---- Configured against device reported

    private static SensorGeometry Reported => new(6248, 4176, 3.76, 3.76);

    [Fact]
    public void WhatTheCameraReports_FillsWhatTheRigDoesNotSay()
    {
        var g = OpticalTrainGeometry.Resolve(new OpticalTrain(750), Reported);

        Assert.Equal(1.034, g.PixelScaleXArcsecPerPixel!.Value, 3);
        Assert.Equal(6248, g.SensorWidthPixels);
        Assert.Equal(GeometrySource.DeviceReported, g.PixelSizeSource);
        Assert.Equal(GeometrySource.DeviceReported, g.SensorPixelsSource);
    }

    [Fact]
    public void AConfiguredValue_WinsOverTheReportedOne_AndSaysSo()
    {
        var g = OpticalTrainGeometry.Resolve(new OpticalTrain(750, null, 4.5, 4.5, null, null), Reported);

        Assert.Equal(4.5, g.PixelSizeXMicrons);
        Assert.Equal(GeometrySource.Configured, g.PixelSizeSource);
        Assert.Equal(GeometrySource.DeviceReported, g.SensorPixelsSource);
        Assert.Equal(6248, g.SensorWidthPixels);
    }

    [Fact]
    public void ARigWithoutOptics_HasNoGeometryEvenWithACamera()
    {
        var g = OpticalTrainGeometry.Resolve(null, Reported);

        Assert.Null(g.FocalLengthMm);
        Assert.Null(g.PixelScaleXArcsecPerPixel);
        Assert.Null(g.FieldOfViewXDegrees);
    }

    [Fact]
    public void ACameraThatReportsNothing_AndARigThatSaysNothing_IsUnknown()
    {
        var g = OpticalTrainGeometry.Resolve(new OpticalTrain(750), SensorGeometry.From(new CameraCapabilities()));

        Assert.Equal(GeometrySource.Unknown, g.PixelSizeSource);
        Assert.Null(g.PixelScaleXArcsecPerPixel);
    }

    [Fact]
    public void TheSensorOfACamera_KeepsBothPixelSizes()
    {
        var sensor = SensorGeometry.From(new CameraCapabilities { SensorWidth = 100, SensorHeight = 50, PixelSizeXMicrons = 3.0, PixelSizeYMicrons = 6.0 })!;

        Assert.Equal((100, 50, 3.0, 6.0), (sensor.WidthPixels, sensor.HeightPixels, sensor.PixelSizeXMicrons, sensor.PixelSizeYMicrons));
    }

    // ---- Hints for a plate solver

    [Fact]
    public void ThePlateSolveHints_CarryTheDerivedValuesAndTheCenterWhenThereIsOne()
    {
        var g = OpticalTrainGeometry.Resolve(new OpticalTrain(750), Reported);
        var center = new CelestialCoordinates(5.5, 22);

        var hints = g.ToPlateSolveHints(center);

        Assert.Equal(center, hints.ApproximateCenter);
        Assert.Equal(g.PixelScaleXArcsecPerPixel, hints.PixelScaleXArcsecPerPixel);
        Assert.Equal(g.FieldOfViewYDegrees, hints.FieldOfViewYDegrees);
        Assert.Equal(750, hints.FocalLengthMm);
        Assert.Null(OpticalTrainGeometry.Resolve(null, null).ToPlateSolveHints().PixelScaleXArcsecPerPixel);
    }

    [Fact]
    public void TheArcsecondConstant_IsTheOneOfTheSpecification()
    {
        Assert.Equal(206264.806247, Sidera.Core.Astronomy.AstronomyConstants.ArcsecondsPerRadian);
        Assert.Equal(206.264806247, Sidera.Core.Astronomy.AstronomyConstants.ArcsecondsPerPixelPerMicrometerPerMillimeter, 9);
    }
}
