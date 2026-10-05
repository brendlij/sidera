using Sidera.Core.Framing;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Rigs;

/// <summary>What the current field is, from the mount and the latest plate solve: live from the mount, exact while a solve is current, and a rotation that is never made up.</summary>
public sealed class CurrentViewResolverTests
{
    private static readonly CelestialCoordinates Mount = new(5.5, 22);
    private static readonly CelestialCoordinates Solved = new(5.5004, 22.003);

    [Fact]
    public void WithoutAMountOrASolve_ThereIsNoCurrentField()
    {
        Assert.Null(CurrentViewResolver.Resolve(null, null, null));
    }

    [Fact]
    public void WithOnlyTheMount_TheFieldIsAtTheMount_AndItsRotationIsUnknown()
    {
        var view = CurrentViewResolver.Resolve(Mount, null, null)!;

        Assert.Equal(Mount, view.Center);
        Assert.Null(view.RotationDegrees);
        Assert.Equal(CurrentViewSource.Mount, view.Source);
    }

    [Fact]
    public void ASolveThatIsCurrent_GivesTheSolvedPositionAndRotation()
    {
        var view = CurrentViewResolver.Resolve(Mount, (Solved, 81.2), mountAtSolve: Mount)!;

        Assert.Equal(Solved, view.Center);
        Assert.Equal(81.2, view.RotationDegrees);
        Assert.Equal(CurrentViewSource.Solved, view.Source);
    }

    [Fact]
    public void OnceTheMountHasMoved_TheMountIsTheLivePosition_AndTheRotationOfTheSolveIsKept()
    {
        var moved = new CelestialCoordinates(6.5, 10);

        var view = CurrentViewResolver.Resolve(moved, (Solved, 81.2), mountAtSolve: Mount)!;

        Assert.Equal(moved, view.Center);
        Assert.Equal(81.2, view.RotationDegrees); // slewing does not turn the camera
        Assert.Equal(CurrentViewSource.Mount, view.Source);
    }

    [Fact]
    public void ASmallMovementOfTheMount_StillCountsAsNotMoved_ButALargeOneDoesNot()
    {
        var jitter = new CelestialCoordinates(5.5 + 0.0003, 22 + 0.005); // well under an arcminute
        var drifted = new CelestialCoordinates(5.5, 22.05); // three arcminutes

        Assert.Equal(CurrentViewSource.Solved, CurrentViewResolver.Resolve(jitter, (Solved, 0), Mount)!.Source);
        Assert.Equal(CurrentViewSource.Mount, CurrentViewResolver.Resolve(drifted, (Solved, 0), Mount)!.Source);
    }

    [Fact]
    public void ASolveWhoseMountPositionWasNotKnown_IsNotTakenForCurrent()
    {
        var view = CurrentViewResolver.Resolve(Mount, (Solved, 10), mountAtSolve: null)!;

        Assert.Equal(CurrentViewSource.Mount, view.Source);
        Assert.Equal(10, view.RotationDegrees);
    }

    [Fact]
    public void WithoutAMount_TheSolveIsAllThereIs()
    {
        var view = CurrentViewResolver.Resolve(null, (Solved, -170), null)!;

        Assert.Equal(Solved, view.Center);
        Assert.Equal(CurrentViewSource.Solved, view.Source);
        Assert.Equal(-170, view.RotationDegrees);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-180, 180)]
    [InlineData(540, 180)]
    public void TheRotationIsTheOneOfSidera_InMinus180To180(double solved, double expected)
    {
        Assert.Equal(expected, CurrentViewResolver.Resolve(Mount, (Solved, solved), Mount)!.RotationDegrees!.Value, 9);
    }

    [Fact]
    public void ARotationThatIsNotANumber_IsUnknown_NotZero()
    {
        var view = CurrentViewResolver.Resolve(Mount, (Solved, double.NaN), Mount)!;

        Assert.Null(view.RotationDegrees);
    }
}
