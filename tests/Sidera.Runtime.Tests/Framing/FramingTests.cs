using Sidera.Core.Astrometry;
using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Tests.Framing;

public sealed class FramingTargetTests
{
    private static readonly CelestialCoordinates M31 = new(0.7123, 41.269);

    [Fact]
    public void ATarget_KeepsWhatWasChosen_AndNoFieldOfView()
    {
        var target = new FramingTarget("  M31 ", M31, 87.5, new RigId("rig.main"), " M31 ", "CDS/P/DSS2/color");

        Assert.Equal("M31", target.Name);
        Assert.Equal(M31, target.Center);
        Assert.Equal(87.5, target.DesiredRotationDegrees);
        Assert.Equal(new RigId("rig.main"), target.RigId);
        Assert.Equal("M31", target.CatalogId);
        Assert.Equal("CDS/P/DSS2/color", target.SurveyId);
        Assert.DoesNotContain(typeof(FramingTarget).GetProperties(), p => p.Name.Contains("FieldOfView", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(87.5, 87.5)]
    [InlineData(180, 180)]
    [InlineData(-180, 180)]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    public void TheDesiredRotation_IsNormalizedLikeTheRotationOfASolve(double input, double expected)
    {
        var target = new FramingTarget("T", M31, input);

        Assert.Equal(expected, target.DesiredRotationDegrees, 9);
        Assert.Equal(SkyMath.NormalizeRotationDegrees(input), target.DesiredRotationDegrees);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ARotationThatIsNotANumber_IsRefused(double rotation) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramingTarget("T", M31, rotation));

    [Fact]
    public void ATargetNeedsAName() => Assert.Throws<ArgumentException>(() => new FramingTarget(" ", M31));

    [Fact]
    public void TheEditsGiveANewTarget_AndKeepTheRest()
    {
        var target = new FramingTarget("T", M31, 10, new RigId("a"), "X", "S");

        var moved = target.WithCenter(new CelestialCoordinates(23.99, -80));
        var turned = target.WithRotation(200);
        var other = target.WithRig(new RigId("b"));

        Assert.Equal(new CelestialCoordinates(23.99, -80), moved.Center);
        Assert.Equal((10.0, "T", new RigId("a")), (moved.DesiredRotationDegrees, moved.Name, moved.RigId));
        Assert.Equal(-160, turned.DesiredRotationDegrees, 9);
        Assert.Equal(new RigId("b"), other.RigId);
        Assert.Equal(10, other.DesiredRotationDegrees);
    }

    [Theory]
    [InlineData(87.5, 81.2, -6.3)]
    [InlineData(81.2, 87.5, 6.3)]
    [InlineData(170, -170, 20)] // across the wrap of the rotation
    [InlineData(-170, 170, -20)]
    [InlineData(10, 10, 0)]
    public void TheDifferenceToASolvedRotation_IsSolvedMinusDesired_AcrossTheWrap(double desired, double solved, double expected)
    {
        var target = new FramingTarget("T", M31, desired);

        Assert.Equal(expected, target.RotationDifferenceDegrees(solved), 9);
    }
}

public sealed class FramingGeometryTests
{
    private static readonly RigField Field = new(2.0, 1.0);

    [Fact]
    public void TheField_ComesFromTheRigsGeometry_AndIsUnknownWithoutIt()
    {
        var geometry = OpticalTrainGeometry.Resolve(new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176), null);

        var field = RigField.From(geometry)!;

        Assert.Equal(geometry.FieldOfViewXDegrees, field.WidthDegrees);
        Assert.Equal(geometry.FieldOfViewYDegrees, field.HeightDegrees);
        Assert.Equal(1.79, field.WidthDegrees, 2);
        Assert.Equal(1.20, field.HeightDegrees, 2);
        Assert.Null(RigField.From(OpticalTrainGeometry.Resolve(new OpticalTrain(750), null)));
        Assert.Null(RigField.From(OpticalTrainGeometry.Resolve(null, null)));
    }

    [Fact]
    public void SwitchingTheRig_GivesAnotherField_WithoutAnythingStored()
    {
        var wide = RigField.From(OpticalTrainGeometry.Resolve(new OpticalTrain(250, null, 3.76, 3.76, 6248, 4176), null))!;
        var narrow = RigField.From(OpticalTrainGeometry.Resolve(new OpticalTrain(1200, null, 3.76, 3.76, 4656, 3520), null))!;

        Assert.True(wide.WidthDegrees > 5 * narrow.WidthDegrees);
        Assert.Equal(wide.WidthDegrees / wide.HeightDegrees, 6248.0 / 4176, 2);
    }

    [Fact]
    public void AnAsymmetricSensor_GivesAnAsymmetricField()
    {
        var field = RigField.From(OpticalTrainGeometry.Compute(1000, 3.0, 6.0, 2000, 500))!;

        // 6 mm by 3 mm behind 1000 mm: 0.34 and 0.17 degrees.
        Assert.Equal(2 * Math.Atan(3.0 / 1000) * 180 / Math.PI, field.WidthDegrees, 9);
        Assert.Equal(2 * Math.Atan(1.5 / 1000) * 180 / Math.PI, field.HeightDegrees, 9);
    }

    [Fact]
    public void AtRotationZero_TheTopIsNorth_AndTheRightIsWest()
    {
        var center = new CelestialCoordinates(6, 20);

        var top = FramingGeometry.PointOnSky(center, Field, 0, 0.5, 0);
        var right = FramingGeometry.PointOnSky(center, Field, 0.5, 0, 0);

        Assert.True(top.DeclinationDegrees > center.DeclinationDegrees);
        Assert.Equal(0.5, top.DeclinationDegrees - center.DeclinationDegrees, 2);
        Assert.True(right.RightAscensionHours < center.RightAscensionHours); // west of the center is smaller right ascension
        Assert.Equal(1.0, SkyMath.AngularSeparationDegrees(center, right), 2);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(45)]
    [InlineData(180)]
    public void ARotation_TurnsTheFrameCounterclockwise_NorthIsThatManyDegreesFromTheTop(double rotation)
    {
        var center = new CelestialCoordinates(6, 20);

        // North in the frame: the plane direction (0, 1). Its frame coordinates: right = north.x*right.x + ..., up = ...
        var (xiUp, etaUp) = FramingGeometry.PlaneOffset(0, 1, rotation);
        var (xiRight, etaRight) = FramingGeometry.PlaneOffset(1, 0, rotation);

        // The top of the frame turned by `rotation` counterclockwise-of-north-towards-east: its angle from north is -rotation (towards west).
        var angleOfTopFromNorth = Math.Atan2(xiUp, etaUp) * 180 / Math.PI; // east positive
        Assert.Equal(SkyMath.NormalizeRotationDegrees(-rotation), SkyMath.NormalizeRotationDegrees(angleOfTopFromNorth), 9);
        Assert.Equal(0, xiUp * xiRight + etaUp * etaRight, 9); // right is at a right angle to up
        Assert.NotNull(center);
    }

    [Fact]
    public void ARotatedFrame_HasTheSameSize_AndItsCornersTurn()
    {
        var center = new CelestialCoordinates(6, 20);
        var square = new RigField(1.0, 1.0); // a quarter turn of a square puts every corner on a corner
        var plain = FramingGeometry.Corners(center, square, 0);
        var turned = FramingGeometry.Corners(center, square, 90);

        Assert.Equal(SkyMath.AngularSeparationDegrees(plain[0], plain[2]), SkyMath.AngularSeparationDegrees(turned[0], turned[2]), 6);
        Assert.Equal(SkyMath.AngularSeparationDegrees(plain[0], plain[1]), SkyMath.AngularSeparationDegrees(turned[0], turned[1]), 6);
        // A quarter turn: the top left corner goes where the top right (or the bottom left) was.
        Assert.True(SkyMath.AngularSeparationDegrees(turned[0], plain[3]) < 0.01 || SkyMath.AngularSeparationDegrees(turned[0], plain[1]) < 0.01);
    }

    [Fact]
    public void AFieldAcrossZeroHours_HasCornersOnBothSides_WithoutAJump()
    {
        var center = new CelestialCoordinates(23.9995, 10);

        var corners = FramingGeometry.Corners(center, new RigField(2, 2), 0);

        Assert.Contains(corners, c => c.RightAscensionHours > 23.9);
        Assert.Contains(corners, c => c.RightAscensionHours < 0.1);
        Assert.All(corners, c => Assert.True(SkyMath.AngularSeparationDegrees(center, c) < 1.5));
    }

    [Fact]
    public void AtAHighDeclination_ARightAscensionBoxWouldBeWrong_ButTheFrameKeepsItsSize()
    {
        var center = new CelestialCoordinates(3, 88);

        var corners = FramingGeometry.Corners(center, new RigField(2, 1), 0);

        // The width is 2 degrees on the sky, which is far more than 2 degrees of right ascension at this declination.
        Assert.Equal(2.0, SkyMath.AngularSeparationDegrees(corners[0], corners[1]), 1);
        Assert.Equal(1.0, SkyMath.AngularSeparationDegrees(corners[1], corners[2]), 1);
        var spanOfRa = Math.Abs(corners[0].RightAscensionHours - corners[1].RightAscensionHours) * 15;
        Assert.True(spanOfRa > 20, $"{spanOfRa} degrees of right ascension for a field of 2 degrees");
    }

    [Fact]
    public void AViewport_PutsTheCenterInTheMiddle_NorthUp_EastLeft()
    {
        var view = new SkyViewport(new CelestialCoordinates(6, 20), 0.01, 400, 300);
        var north = new CelestialCoordinates(6, 20.5);
        var east = SkyMath.FromTangentOffset(view.Center, 0.5, 0);

        var middle = view.ToPixel(view.Center)!.Value;
        var northPixel = view.ToPixel(north)!.Value;
        var eastPixel = view.ToPixel(east)!.Value;

        Assert.Equal((200.0, 150.0), middle);
        Assert.True(northPixel.Y < middle.Y); // north is up
        Assert.True(eastPixel.X < middle.X); // east is left
        Assert.Equal(50, middle.Y - northPixel.Y, 1);
    }

    [Theory]
    [InlineData(6, 20)]
    [InlineData(23.99, -5)]
    [InlineData(0.01, 60)]
    [InlineData(12, 88)]
    public void APixelAndItsPosition_AreInverse(double ra, double dec)
    {
        var view = new SkyViewport(new CelestialCoordinates(ra, dec), 0.005, 800, 600);

        foreach (var (x, y) in new[] { (0.0, 0.0), (800.0, 600.0), (123.5, 456.5), (400.0, 300.0) })
        {
            var sky = view.ToSky(x, y);
            var back = view.ToPixel(sky)!.Value;
            Assert.Equal(x, back.X, 6);
            Assert.Equal(y, back.Y, 6);
        }
    }

    [Fact]
    public void DraggingTheFrame_MovesItsCenterByTheDraggedAngle_NotByADifferenceOfCoordinates()
    {
        // Near the pole 10 pixels of drag are a different difference of right ascension than at the equator; the angle on the sky is what counts.
        foreach (var dec in new[] { 0.0, 60.0, 85.0 })
        {
            var view = new SkyViewport(new CelestialCoordinates(6, dec), 0.01, 800, 600);
            var dragged = view.ToSky(400 - 100, 300); // the frame is dragged 100 pixels to the left of the middle: 1 degree to the east

            Assert.Equal(1.0, SkyMath.AngularSeparationDegrees(view.Center, dragged), 2);
        }
    }

    [Fact]
    public void ThePanOfAView_KeepsThePointUnderThePointer()
    {
        var view = new SkyViewport(new CelestialCoordinates(0.001, 30), 0.01, 800, 600);
        var under = view.ToSky(300, 200);

        var panned = view.Panned(50, -20);
        var after = panned.ToPixel(under)!.Value;

        // The plane of the new view is another plane: over 50 pixels of a 0.01 degree scale the point moves by a fraction of a pixel, not more.
        Assert.InRange(Math.Abs(after.X - (300 + 50)), 0, 1);
        Assert.InRange(Math.Abs(after.Y - (200 - 20)), 0, 1);
    }

    [Fact]
    public void TheOutlineOfAFrame_IsAClosedQuadrilateralAroundItsCenter()
    {
        var view = new SkyViewport(new CelestialCoordinates(6, 20), 0.005, 800, 600);

        var outline = view.Outline(view.Center, new RigField(2, 1), 0);

        Assert.Equal(4, outline.Count);
        Assert.Equal((200.0, 200.0), (Math.Round(outline[0].X), Math.Round(outline[0].Y)));
        Assert.Equal((600.0, 400.0), (Math.Round(outline[2].X), Math.Round(outline[2].Y)));
    }

    [Fact]
    public void TheFrameOfARotatedTarget_CanBeComparedWithASolvedRotation()
    {
        var target = new FramingTarget("T", new CelestialCoordinates(5, 30), 87.5);
        var solved = new PlateSolveResult { Success = true, RotationDegrees = 81.2 };

        Assert.Equal(-6.3, target.RotationDifferenceDegrees(solved.RotationDegrees!.Value), 9);
    }
}
