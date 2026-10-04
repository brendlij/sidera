using Sidera.Core.Devices;

namespace Sidera.Runtime.Tests.Devices;

/// <summary>
/// What an exposure asks of the camera, resolved against what the connected camera supports: the explicit value, else the
/// camera default, else the camera as it is; nothing is coerced, and a camera that cannot be asked yet is not wrong.
/// </summary>
public class AcquisitionResolverTests
{
    private static CameraCapabilities Caps(Func<CameraCapabilities, CameraCapabilities>? change = null)
    {
        var caps = new CameraCapabilities
        {
            SensorWidth = 800,
            SensorHeight = 600,
            MaxAdu = 65535,
            MaxBinX = 4,
            MaxBinY = 4,
            SupportsSubframe = true,
            Gain = IntegerControl.Range(0, 100),
            Offset = IntegerControl.Range(0, 50),
            ReadoutModes = ["Normal", "Slow"],
            CanFastReadout = true,
            HasShutter = true,
            MinExposureSeconds = 0.001,
            MaxExposureSeconds = 3600,
        };
        return change?.Invoke(caps) ?? caps;
    }

    private static CameraSettings Current(Func<CameraSettings, CameraSettings>? change = null)
    {
        var settings = new CameraSettings
        {
            Gain = 10, Offset = 5, BinX = 1, BinY = 1, StartX = 0, StartY = 0, NumX = 800, NumY = 600, ReadoutMode = 0, FastReadout = false,
        };
        return change?.Invoke(settings) ?? settings;
    }

    private static AcquisitionPlan Resolve(
        AcquisitionIntent? intent = null, AcquisitionIntent? defaults = null, CameraCapabilities? caps = null, CameraSettings? current = null,
        TimeSpan? duration = null) =>
        AcquisitionResolver.Resolve(
            intent ?? AcquisitionIntent.Default, defaults, duration ?? TimeSpan.FromSeconds(300),
            DeviceCapabilities<CameraCapabilities>.Of(caps ?? Caps()), current ?? Current());

    // ---- Resolution

    [Fact]
    public void AnExposureThatSetsNothing_OnACameraWithoutDefaults_ChangesNothing()
    {
        var plan = Resolve();

        Assert.True(plan.IsValid);
        Assert.True(plan.Change.IsEmpty);
        Assert.Equal(10, plan.Effective.Gain);
        Assert.Equal(FrameType.Light, plan.FrameType);
    }

    [Fact]
    public void WhatTheExposureDoesNotSet_IsInheritedFromTheCameraDefaults()
    {
        var plan = Resolve(defaults: new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50), BinX = 2, BinY = 2, ReadoutMode = "Slow" });

        Assert.True(plan.IsValid);
        Assert.Equal(50, plan.Change.Gain);
        Assert.Equal((2, 2), (plan.Change.BinX, plan.Change.BinY));
        Assert.Equal(1, plan.Change.ReadoutMode);
        Assert.Null(plan.Change.Offset);
    }

    [Fact]
    public void AnExplicitValue_WinsOverTheDefault_AndTheRestStaysInherited()
    {
        var plan = Resolve(
            new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(20) },
            new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50), Offset = AcquisitionLevel.OfNumber(9) });

        Assert.Equal(20, plan.Change.Gain);
        Assert.Equal(9, plan.Change.Offset);
        Assert.Equal(20, plan.Effective.Gain);
    }

    [Fact]
    public void AllExplicit_NeedsNoDefault()
    {
        var plan = Resolve(new AcquisitionIntent
        {
            FrameType = FrameType.Flat, Gain = AcquisitionLevel.OfNumber(0), Offset = AcquisitionLevel.OfNumber(0), BinX = 2, BinY = 2,
            Region = AcquisitionRegion.Of(10, 20, 100, 50), ReadoutMode = "Slow", FastReadout = true,
        });

        Assert.True(plan.IsValid);
        Assert.Equal(FrameType.Flat, plan.FrameType);
        Assert.Equal((0, 0, 2, 2), (plan.Change.Gain, plan.Change.Offset, plan.Change.BinX, plan.Change.BinY));
        Assert.Equal((10, 20, 100, 50), (plan.Change.StartX, plan.Change.StartY, plan.Change.NumX, plan.Change.NumY));
        Assert.Equal((true, 1), (plan.Change.FastReadout, plan.Change.ReadoutMode));
        Assert.Equal(("Slow", 100, 50), (plan.Effective.ReadoutMode, plan.Effective.Width, plan.Effective.Height));
    }

    [Fact]
    public void ValuesThatAreAlreadyOnTheCamera_AreNotWrittenAgain()
    {
        var plan = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(10), BinX = 1, BinY = 1, ReadoutMode = "Normal" });

        Assert.True(plan.IsValid);
        Assert.True(plan.Change.IsEmpty);
    }

    [Fact]
    public void ACameraThatIsNotConnected_CannotBeChecked_AndIsNotWrongForThat()
    {
        var plan = AcquisitionResolver.Resolve(
            new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(5000) }, null, TimeSpan.FromSeconds(1),
            DeviceCapabilities<CameraCapabilities>.Unknown, null);

        Assert.Equal(AcquisitionStatus.NotVerifiable, plan.Status);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void WhenTheCapabilitiesChangeAfterAReconnect_TheSameIntentIsCheckedAgain()
    {
        var intent = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(300) };
        Assert.False(Resolve(intent).IsValid);

        var after = Resolve(intent, caps: Caps(c => c with { Gain = IntegerControl.Range(0, 500) }));

        Assert.True(after.IsValid);
        Assert.Equal(300, after.Change.Gain);
    }

    // ---- Gain and offset

    [Fact]
    public void ANumericGain_IsCheckedAgainstItsRange_NeverCoerced()
    {
        Assert.True(Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(0) }).IsValid);
        Assert.True(Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(100) }).IsValid);

        var over = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(300) });

        Assert.Equal(AcquisitionStatus.Invalid, over.Status);
        Assert.Contains("rejects gain 300", over.Problems[0]);
        Assert.Contains("0 to 100", over.Problems[0]);
        Assert.Equal(10, over.Effective.Gain);
    }

    [Fact]
    public void ANamedGain_IsFoundByNameAndAppliedAsItsIndex()
    {
        var caps = Caps(c => c with { Gain = IntegerControl.List(["Low", "High", "HDR"]) });

        var plan = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfName("HDR") }, caps: caps, current: Current(s => s with { Gain = 0 }));

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.Change.Gain);
        Assert.Equal("HDR", plan.Effective.GainName);
    }

    [Fact]
    public void ANamedGain_ThatTheCameraDoesNotHave_OrANumberForAList_IsRefusedWithWhatIsOffered()
    {
        var caps = Caps(c => c with { Gain = IntegerControl.List(["Low", "High"]) });

        var missing = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfName("Ultra") }, caps: caps);
        var number = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(1) }, caps: caps);
        var nameForRange = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfName("High") });

        Assert.Contains("no gain 'Ultra'", missing.Problems[0]);
        Assert.Contains("Low, High", missing.Problems[0]);
        Assert.Equal(AcquisitionStatus.Invalid, number.Status);
        Assert.Equal(AcquisitionStatus.Invalid, nameForRange.Status);
    }

    [Fact]
    public void GainAndOffsetThatTheCameraDoesNotHave_AreRefused()
    {
        var caps = Caps(c => c with { Gain = null, Offset = null });

        var gain = Resolve(new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(5) }, caps: caps, current: Current(s => s with { Gain = null, Offset = null }));
        var offset = Resolve(new AcquisitionIntent { Offset = AcquisitionLevel.OfNumber(5) }, caps: caps, current: Current(s => s with { Gain = null, Offset = null }));

        Assert.Contains("no gain setting", gain.Problems[0]);
        Assert.Contains("no offset setting", offset.Problems[0]);
    }

    [Fact]
    public void ANumericAndANamedOffset_FollowTheSameRules()
    {
        Assert.True(Resolve(new AcquisitionIntent { Offset = AcquisitionLevel.OfNumber(50) }).IsValid);
        Assert.False(Resolve(new AcquisitionIntent { Offset = AcquisitionLevel.OfNumber(51) }).IsValid);
        var caps = Caps(c => c with { Offset = IntegerControl.List(["Standard", "High"]) });
        Assert.Equal(1, Resolve(new AcquisitionIntent { Offset = AcquisitionLevel.OfName("High") }, caps: caps, current: Current(s => s with { Offset = 0 })).Change.Offset);
        Assert.False(Resolve(new AcquisitionIntent { Offset = AcquisitionLevel.OfName("Low") }, caps: caps).IsValid);
    }

    [Fact]
    public void ADefaultThatNoLongerFits_IsReportedAsTheCameraDefault_NotChanged()
    {
        var plan = Resolve(defaults: new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(300) });

        Assert.Equal(AcquisitionStatus.Invalid, plan.Status);
        Assert.Contains("(the camera default)", plan.Problems[0]);
        Assert.Null(plan.Change.Gain);
    }

    // ---- Binning

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(0, false)]
    public void Binning_IsCheckedAgainstTheMaximum(int bin, bool valid)
    {
        var plan = Resolve(new AcquisitionIntent { BinX = bin, BinY = bin });

        Assert.Equal(valid, plan.IsValid);
    }

    [Fact]
    public void ACameraThatBinsTheSameOnBothAxes_TakesOneAxisAsBoth_AndRefusesAnAsymmetricRequest()
    {
        var one = Resolve(new AcquisitionIntent { BinX = 2 });
        var asymmetric = Resolve(new AcquisitionIntent { BinX = 1, BinY = 2 });

        Assert.True(one.IsValid);
        Assert.Equal((2, 2), (one.Effective.BinX, one.Effective.BinY));
        Assert.False(asymmetric.IsValid);
        Assert.Contains("same horizontally and vertically", asymmetric.Problems[0]);
    }

    [Fact]
    public void ACameraThatBinsAsymmetrically_KeepsTheOtherAxisAsItIs()
    {
        var caps = Caps(c => c with { CanAsymmetricBin = true });

        var plan = Resolve(new AcquisitionIntent { BinX = 2 }, caps: caps);

        Assert.True(plan.IsValid);
        Assert.Equal((2, 1), (plan.Effective.BinX, plan.Effective.BinY));
        Assert.True(Resolve(new AcquisitionIntent { BinX = 1, BinY = 3 }, caps: caps).IsValid);
    }

    [Fact]
    public void ACameraThatDoesNotBin_RefusesABinning()
    {
        var caps = Caps(c => c with { MaxBinX = 1, MaxBinY = 1 });

        Assert.True(Resolve(new AcquisitionIntent { BinX = 1, BinY = 1 }, caps: caps).IsValid);
        Assert.False(Resolve(new AcquisitionIntent { BinX = 2, BinY = 2 }, caps: caps).IsValid);
    }

    // ---- Region

    [Fact]
    public void TheFullFrame_IsTheWholeSensorAtTheBinningOfTheExposure()
    {
        var plan = Resolve(
            new AcquisitionIntent { BinX = 2, BinY = 2, Region = AcquisitionRegion.Full },
            current: Current(s => s with { StartX = 10, StartY = 10, NumX = 100, NumY = 100 }));

        Assert.True(plan.IsValid);
        Assert.Equal((0, 0, 400, 300), (plan.Effective.StartX, plan.Effective.StartY, plan.Effective.Width, plan.Effective.Height));
    }

    [Fact]
    public void ANewBinningWithoutARegion_MeansTheWholeSensorAtThatBinning_NeverAStaleRegion()
    {
        var plan = Resolve(
            new AcquisitionIntent { BinX = 2, BinY = 2 }, current: Current(s => s with { StartX = 10, StartY = 10, NumX = 100, NumY = 100 }));

        Assert.True(plan.IsValid);
        Assert.Equal((400, 300), (plan.Effective.Width, plan.Effective.Height));
    }

    [Fact]
    public void ARegionThatLiesInsideTheSensor_IsAppliedAsItIs()
    {
        var plan = Resolve(new AcquisitionIntent { Region = AcquisitionRegion.Of(100, 50, 200, 120) });

        Assert.True(plan.IsValid);
        Assert.Equal((100, 50, 200, 120), (plan.Change.StartX, plan.Change.StartY, plan.Change.NumX, plan.Change.NumY));
    }

    [Theory]
    [InlineData(700, 0, 200, 100)]
    [InlineData(0, 550, 100, 100)]
    [InlineData(0, 0, 801, 100)]
    [InlineData(0, 0, 100, 601)]
    public void ARegionOutsideTheSensor_IsRefused_NotShrunk(int x, int y, int w, int h)
    {
        var plan = Resolve(new AcquisitionIntent { Region = AcquisitionRegion.Of(x, y, w, h) });

        Assert.Equal(AcquisitionStatus.Invalid, plan.Status);
        Assert.Contains("does not lie inside", plan.Problems[0]);
    }

    [Fact]
    public void ARegionIsCheckedAtTheBinningItIsUsedWith()
    {
        var region = AcquisitionRegion.Of(0, 0, 500, 100);

        Assert.True(Resolve(new AcquisitionIntent { Region = region }).IsValid);
        Assert.False(Resolve(new AcquisitionIntent { Region = region, BinX = 2, BinY = 2 }).IsValid);
    }

    [Fact]
    public void ARegionThatWasFineBefore_IsRefusedWhenTheNewCameraIsSmaller()
    {
        var region = new AcquisitionIntent { Region = AcquisitionRegion.Of(0, 0, 700, 500) };
        Assert.True(Resolve(region).IsValid);

        var smaller = Resolve(region, caps: Caps(c => c with { SensorWidth = 400, SensorHeight = 300 }), current: Current(s => s with { NumX = 400, NumY = 300 }));

        Assert.False(smaller.IsValid);
    }

    [Fact]
    public void ACameraWithoutSubframe_RefusesARegion()
    {
        var plan = Resolve(new AcquisitionIntent { Region = AcquisitionRegion.Full }, caps: Caps(c => c with { SupportsSubframe = false }));

        Assert.Contains("no subframe", plan.Problems[0]);
    }

    [Theory]
    [InlineData(-1)]
    public void ARegionCannotStartBelowZero(int x) => Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionRegion.Of(x, 0, 10, 10));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void ARegionMustHaveASize(int w, int h) => Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionRegion.Of(0, 0, w, h));

    // ---- Readout

    [Fact]
    public void AReadoutModeIsFoundByName_AndInheritedWhenNotSet()
    {
        Assert.Equal(1, Resolve(new AcquisitionIntent { ReadoutMode = "Slow" }).Change.ReadoutMode);
        Assert.Null(Resolve().Change.ReadoutMode);
    }

    [Fact]
    public void AReadoutModeThatNoLongerExists_IsRefused_AndNoOtherModeIsChosen()
    {
        var plan = Resolve(new AcquisitionIntent { ReadoutMode = "Turbo" });

        Assert.Equal(AcquisitionStatus.Invalid, plan.Status);
        Assert.Contains("no readout mode 'Turbo'", plan.Problems[0]);
        Assert.Contains("Normal, Slow", plan.Problems[0]);
        Assert.Null(plan.Change.ReadoutMode);
    }

    [Fact]
    public void AMissingModeAfterAReconnect_IsFoundOut()
    {
        var intent = new AcquisitionIntent { ReadoutMode = "Slow" };
        Assert.True(Resolve(intent).IsValid);

        Assert.False(Resolve(intent, caps: Caps(c => c with { ReadoutModes = ["Normal"] })).IsValid);
        Assert.False(Resolve(intent, caps: Caps(c => c with { ReadoutModes = [] })).IsValid);
    }

    [Fact]
    public void FastReadout_IsOnlyOfferedByACameraThatHasIt_AndNotInferredFromModeNames()
    {
        var caps = Caps(c => c with { CanFastReadout = false, ReadoutModes = ["Fast", "Normal"] });

        var plan = Resolve(new AcquisitionIntent { FastReadout = true }, caps: caps);

        Assert.Contains("no fast readout", plan.Problems[0]);
        Assert.True(Resolve(new AcquisitionIntent { FastReadout = true }).IsValid);
    }

    // ---- Frame type and duration

    [Fact]
    public void ADarkAndABias_NeedAShutter_ALightAndAFlatDoNot()
    {
        var noShutter = Caps(c => c with { HasShutter = false });

        Assert.True(Resolve(new AcquisitionIntent { FrameType = FrameType.Dark }).IsValid);
        Assert.True(Resolve(new AcquisitionIntent { FrameType = FrameType.Bias }).IsValid);
        Assert.False(Resolve(new AcquisitionIntent { FrameType = FrameType.Dark }, caps: noShutter).IsValid);
        Assert.False(Resolve(new AcquisitionIntent { FrameType = FrameType.Bias }, caps: noShutter).IsValid);
        Assert.True(Resolve(new AcquisitionIntent { FrameType = FrameType.Light }, caps: noShutter).IsValid);
        Assert.True(Resolve(new AcquisitionIntent { FrameType = FrameType.Flat }, caps: noShutter).IsValid);
    }

    [Fact]
    public void ABias_IsNotJustAnExposureOfZeroSeconds()
    {
        // The frame type is its own intent; the duration is still the duration that was asked for.
        var plan = Resolve(new AcquisitionIntent { FrameType = FrameType.Bias }, duration: TimeSpan.FromSeconds(0.001));

        Assert.True(plan.IsValid);
        Assert.Equal(FrameType.Bias, plan.Effective.FrameType);
    }

    [Fact]
    public void TheDuration_MustBeInsideWhatTheCameraCanDo()
    {
        Assert.False(Resolve(duration: TimeSpan.FromSeconds(7200)).IsValid);
        Assert.False(Resolve(duration: TimeSpan.FromMilliseconds(0.1)).IsValid);
        Assert.True(Resolve(duration: TimeSpan.FromSeconds(0.5)).IsValid);
    }

    [Fact]
    public void TheIntentKnowsWhetherItSetsAnything()
    {
        Assert.True(AcquisitionIntent.Default.IsDefault);
        Assert.False(AcquisitionIntent.Default.HasOverrides);
        Assert.False(new AcquisitionIntent { FrameType = FrameType.Dark }.IsDefault);
        Assert.False(new AcquisitionIntent { FrameType = FrameType.Dark }.HasOverrides);
        Assert.True(new AcquisitionIntent { BinX = 2 }.HasOverrides);
    }
}
