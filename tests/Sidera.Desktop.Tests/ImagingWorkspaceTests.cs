using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Focusing;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Desktop.Imaging;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;

namespace Sidera.Desktop.Tests;

/// <summary>Manual capture, the view and the saving of the frame, and the manual autofocus of the imaging page, on simulated equipment.</summary>
public sealed class ImagingWorkspaceTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sidera-imaging-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private sealed record Setup(SideraRuntimeHost Host, ImagingViewModel Imaging, ImagingCaptureViewModel Capture, ManualAutofocusViewModel Autofocus);

    // Rigs "Main" and "Wide" with their own camera and focuser, on the same mount and guider; a camera that is in no rig.
    private async Task<Setup> CreateAsync(bool connect = true)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedCamera(new("camera.main"), "Main Camera", 1);
        host.AddSimulatedCamera(new("camera.wide"), "Wide Camera", 2);
        host.AddSimulatedCamera(new("camera.spare"), "Spare Camera", 3);
        host.AddSimulatedFocuser(new("focuser.main"), "Main Focuser", 19500, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedFocuser(new("focuser.wide"), "Wide Focuser", 5500, maxPosition: 12000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedMount(new("mount.eq"), "EQ Mount", TimeSpan.FromMilliseconds(1));
        host.AddSimulatedGuider(new("guider.main"), "Guider", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        var optics = new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176);
        host.AddRig(new Rig(new("rig.main"), "Main Rig", new("camera.main"), optics, new("focuser.main"), null, null, null, new("mount.eq"), new("guider.main")));
        host.AddRig(new Rig(new("rig.wide"), "Wide Rig", new("camera.wide"), new OpticalTrain(250), new("focuser.wide"), null, null, null, new("mount.eq"), new("guider.main")));
        host.AddSimulatedFocusModel(new("rig.main"), new SimulatedFocusModel(20000));
        host.AddSimulatedFocusModel(new("rig.wide"), new SimulatedFocusModel(6000));
        if (connect)
        {
            foreach (var device in host.DeviceRegistry.GetAll())
            {
                await device.ConnectAsync();
            }
        }

        var imaging = new ImagingViewModel(host.FrameAnalyzer, a => a()) { ExportHost = host };
        var capture = new ImagingCaptureViewModel(host, imaging, 0.05);
        var autofocus = new ManualAutofocusViewModel(host, SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry));
        imaging.Capture = capture;
        imaging.Autofocus = autofocus;
        autofocus.ExposureText = "0.04";
        return new Setup(host, imaging, capture, autofocus);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    // ---- Manual capture

    [Fact]
    public async Task TheCaptureOffersTheRigs_AndTheCamerasThatAreInNone()
    {
        var s = await CreateAsync();

        Assert.Equal(["Main Rig", "Wide Rig", "Spare Camera (no rig)"], s.Capture.Targets.Select(t => t.Label));
        Assert.Equal(new DeviceId("camera.main"), s.Capture.SelectedTarget!.CameraId);
        Assert.Equal(new RigId("rig.main"), s.Capture.SelectedTarget.RigId);
        Assert.Null(s.Capture.Targets[2].RigId);
    }

    [Fact]
    public async Task ACapture_UsesTheCameraOfTheSelectedRig_AndShowsTheFrameOnThePage()
    {
        var s = await CreateAsync();
        s.Capture.SelectedTarget = s.Capture.Targets.Single(t => t.Label == "Wide Rig");

        await s.Capture.CaptureCommand.ExecuteAsync(null);

        Assert.NotNull(s.Imaging.LatestFrame);
        Assert.Equal(new DeviceId("camera.wide"), s.Imaging.LatestCameraId);
        Assert.Contains("Wide Rig", s.Imaging.SourceText);
        Assert.Contains("(manual)", s.Imaging.SourceText);
        Assert.Equal(1, s.Imaging.FrameCount);
        Assert.False(s.Capture.IsCapturing);
        Assert.False(s.Capture.HasError);
        Assert.Contains("light frame", s.Capture.StatusText);
    }

    [Fact]
    public async Task ACapture_GoesThroughTheAcquisitionPipeline_SoTheFrameSaysHowItWasTaken()
    {
        var s = await CreateAsync();

        await s.Capture.CaptureCommand.ExecuteAsync(null);

        Assert.NotNull(s.Imaging.LatestFrame!.Acquisition);
        Assert.Equal(FrameType.Light, s.Imaging.LatestFrame.Acquisition!.FrameType);
        Assert.Equal(TimeSpan.FromSeconds(0.05), s.Imaging.LatestFrame.ExposureDuration);
    }

    [Fact]
    public async Task OnlyWhatTheCameraSupports_IsOffered()
    {
        var s = await CreateAsync();
        var capabilities = ((ICameraControl)s.Host.DeviceRegistry.GetAll().OfType<SimulatedCamera>().First(c => c.Id == new DeviceId("camera.main"))).Capabilities.Value!;

        Assert.Equal(capabilities.Gain is not null, s.Capture.ShowGain);
        Assert.Equal(capabilities.Offset is not null, s.Capture.ShowOffset);
        Assert.Equal(capabilities.SupportsBinning, s.Capture.ShowBinning);
        if (capabilities.SupportsBinning)
        {
            Assert.Equal(1, s.Capture.Binnings[0].Value);
            Assert.All(s.Capture.Binnings, b => Assert.InRange(b.Value, 1, capabilities.MaxBinX));
        }
    }

    [Fact]
    public async Task WithoutAConnectedCamera_CaptureIsDisabled_WithTheReason()
    {
        var s = await CreateAsync(connect: false);

        Assert.False(s.Capture.CaptureCommand.CanExecute(null));
        Assert.Contains("Connect Main Camera", s.Capture.DisabledText);
        Assert.False(s.Capture.ShowGain); // nothing is known about a camera that is not connected
        Assert.False(s.Capture.ShowBinning);
    }

    [Fact]
    public async Task AnExposureThatIsNotANumber_IsRefusedWithoutTouchingTheCamera()
    {
        var s = await CreateAsync();
        s.Capture.ExposureText = "long";

        await s.Capture.CaptureCommand.ExecuteAsync(null);

        Assert.True(s.Capture.HasError);
        Assert.Contains("Exposure must be a number of seconds", s.Capture.ErrorText);
        Assert.Null(s.Imaging.LatestFrame);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    public async Task ANonsensicalExposure_IsNotBuilt(string text)
    {
        var s = await CreateAsync();
        s.Capture.ExposureText = text;

        Assert.False(s.Capture.TryBuildIntent(out _, out _, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task TheIntent_HasOnlyWhatWasEntered_TheRestIsTheCamerasOwn()
    {
        var s = await CreateAsync();

        Assert.True(s.Capture.TryBuildIntent(out var plain, out var duration, out _));
        Assert.Equal(TimeSpan.FromSeconds(0.05), duration);
        Assert.True(plain.IsDefault);

        s.Capture.SelectedFrameType = s.Capture.FrameTypes.Single(t => t.Value == FrameType.Flat);
        Assert.True(s.Capture.TryBuildIntent(out var flat, out _, out _));
        Assert.Equal(FrameType.Flat, flat.FrameType);
        Assert.False(flat.HasOverrides);

        if (s.Capture.ShowGain)
        {
            s.Capture.GainText = "120";
            Assert.True(s.Capture.TryBuildIntent(out var gained, out _, out _));
            Assert.Equal(120, gained.Gain!.Number);
            s.Capture.GainText = "much";
            Assert.False(s.Capture.TryBuildIntent(out _, out _, out var problem));
            Assert.Contains("Gain", problem);
        }
    }

    [Fact]
    public async Task ACaptureCanBeCancelled_AndNoFrameIsShownForIt()
    {
        var s = await CreateAsync();
        s.Capture.ExposureText = "30";

        var capture = s.Capture.CaptureCommand.ExecuteAsync(null);
        await WaitAsync(() => s.Capture.IsCapturing, "the exposure");
        Assert.False(s.Capture.CaptureCommand.CanExecute(null));
        Assert.True(s.Capture.CancelCommand.CanExecute(null));
        s.Capture.CancelCommand.Execute(null);
        await capture;

        Assert.Equal("Cancelled.", s.Capture.StatusText);
        Assert.Null(s.Imaging.LatestFrame);
        Assert.False(s.Capture.IsCapturing);
        using var lease = await s.Host.ResourceManager.AcquireAsync([ResourceId.ForDevice(new("camera.main"))], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ACapture_RespectsTheCameraLease_AndDoesNotOverlapAnotherExposureOfTheSameCamera()
    {
        var s = await CreateAsync();
        var held = await s.Host.ResourceManager.AcquireAsync([ResourceId.ForDevice(new("camera.main"))], CancellationToken.None);

        var capture = s.Capture.CaptureCommand.ExecuteAsync(null);
        await Task.Delay(150);
        Assert.Null(s.Imaging.LatestFrame); // waiting for the camera that something else holds

        held.Dispose();
        await capture.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(s.Imaging.LatestFrame);
    }

    [Fact]
    public async Task TheFrameKnowsWhereTheMountPointed_WhenItWasTaken()
    {
        var s = await CreateAsync();
        var mount = s.Host.DeviceRegistry.GetAll().OfType<IMount>().Single();
        await mount.SlewToAsync(new CelestialCoordinates(5.5, 22));

        await s.Capture.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(5.5 * 15, s.Imaging.LatestCapture!.RightAscensionDegrees!.Value, 6);
        Assert.Equal(22, s.Imaging.LatestCapture.DeclinationDegrees!.Value, 6);
        Assert.NotNull(s.Imaging.LatestCapture.ObservedAt);
    }

    [Fact]
    public async Task ARigWithoutAConnectedMount_HasNoPointing_NothingIsMadeUp()
    {
        var s = await CreateAsync();
        await s.Host.DeviceRegistry.GetAll().OfType<IMount>().Single().DisconnectAsync();

        await s.Capture.CaptureCommand.ExecuteAsync(null);

        Assert.Null(s.Imaging.LatestCapture!.RightAscensionDegrees);
        Assert.Null(s.Imaging.LatestCapture.DeclinationDegrees);
    }

    // ---- The view

    [Fact]
    public async Task TheStretchIsOn_ByDefault_AndFitAndOneToOneClearTheFreeView()
    {
        var s = await CreateAsync();

        Assert.True(s.Imaging.AutoStretch);
        Assert.True(s.Imaging.IsFitToView);

        s.Imaging.IsCustomView = true; // the viewer sets this when the person zooms or pans
        Assert.False(s.Imaging.IsFitToView);
        Assert.False(s.Imaging.IsActualSize);

        s.Imaging.ActualSizeCommand.Execute(null);
        Assert.True(s.Imaging.IsActualSize);
        Assert.False(s.Imaging.IsCustomView);

        s.Imaging.IsCustomView = true;
        s.Imaging.FitCommand.Execute(null);
        Assert.True(s.Imaging.IsFitToView);
    }

    // ---- Saving

    [Fact]
    public async Task SavingFits_WritesTheDataAsTaken_WithWhatIsKnown_AndSaysSo()
    {
        var s = await CreateAsync();
        s.Imaging.ExportSite = () => new ObservingSite(48.1, 11.6, 520);
        await s.Capture.CaptureCommand.ExecuteAsync(null);
        var frame = s.Imaging.LatestFrame!;
        var path = Path.Combine(_folder, "frame.fits");

        Assert.True(s.Imaging.SaveFits(path));

        var bytes = File.ReadAllBytes(path);
        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, 2880);
        foreach (var card in new[] { "EXPTIME", "INSTRUME= 'Main Camera", "TELESCOP= 'Main Rig", "FOCALLEN", "XPIXSZ", "SITELAT", "SITELONG", "SITEELEV", "IMAGETYP= 'Light Frame", "DATE-OBS", "RA      =", "DEC     =" })
        {
            Assert.Contains(card, header);
        }

        // The first pixel row in the file is the bottom row of the frame, as it was taken.
        var bottom = (frame.Height - 1) * frame.Width;
        Assert.Equal(frame.Pixels.Span[bottom], (short)(bytes[2880] << 8 | bytes[2881]) + 32768);
        Assert.Contains("not stretched", s.Imaging.SaveStatusText);
    }

    [Fact]
    public async Task TheFitsIsTheSame_WhetherOrNotTheStretchIsShown()
    {
        var s = await CreateAsync();
        await s.Capture.CaptureCommand.ExecuteAsync(null);

        s.Imaging.AutoStretch = true;
        s.Imaging.SaveFits(Path.Combine(_folder, "a.fits"));
        s.Imaging.AutoStretch = false;
        s.Imaging.SaveFits(Path.Combine(_folder, "b.fits"));

        Assert.Equal(File.ReadAllBytes(Path.Combine(_folder, "a.fits")).Skip(2880), File.ReadAllBytes(Path.Combine(_folder, "b.fits")).Skip(2880));
    }

    [Fact]
    public async Task SavingPng_IsAPictureOfTheDisplay_AndSaysWhichStretch()
    {
        var s = await CreateAsync();
        await s.Capture.CaptureCommand.ExecuteAsync(null);
        var stretched = Path.Combine(_folder, "s.png");
        var linear = Path.Combine(_folder, "l.png");

        s.Imaging.AutoStretch = true;
        Assert.True(s.Imaging.SavePng(stretched));
        Assert.Contains("auto stretched", s.Imaging.SaveStatusText);
        Assert.Contains("not the data", s.Imaging.SaveStatusText);
        s.Imaging.AutoStretch = false;
        Assert.True(s.Imaging.SavePng(linear));
        Assert.Contains("linear", s.Imaging.SaveStatusText);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(stretched).Take(4));
        Assert.NotEqual(File.ReadAllBytes(stretched), File.ReadAllBytes(linear));
    }

    [Fact]
    public async Task TheSuggestedNames_SayWhatIsSaved()
    {
        var s = await CreateAsync();
        await s.Capture.CaptureCommand.ExecuteAsync(null);
        var frame = s.Imaging.LatestFrame!;
        var when = new DateTime(2026, 10, 5, 20, 15, 30);

        Assert.Equal("Sidera_light_20261005_201530_0.05s.fits", FrameExporter.SuggestName(frame, "fits", when));
        Assert.Equal("Sidera_light_20261005_201530_0.05s_display_stretched.png", FrameExporter.SuggestName(frame, "png", when, autoStretch: true));
        Assert.EndsWith("_display_linear.png", FrameExporter.SuggestName(frame, ".png", when, autoStretch: false));
    }

    [Fact]
    public async Task TheSaveCommands_AskWhereToSave_AndDoNothingWhenTheyAreCancelled()
    {
        var s = await CreateAsync();
        await s.Capture.CaptureCommand.ExecuteAsync(null);
        var asked = new List<(string Name, string Kind)>();
        s.Imaging.PickSavePath = (name, kind) =>
        {
            asked.Add((name, kind));
            return Task.FromResult<string?>(kind == "fits" ? Path.Combine(_folder, "picked.fits") : null);
        };

        await s.Imaging.SaveFitsCommand.ExecuteAsync(null);
        await s.Imaging.SavePngCommand.ExecuteAsync(null);

        Assert.Equal(["fits", "png"], asked.Select(a => a.Kind));
        Assert.True(File.Exists(Path.Combine(_folder, "picked.fits")));
        Assert.False(File.Exists(Path.Combine(_folder, "picked.png")));
    }

    [Fact]
    public async Task WithoutAFrame_NothingCanBeSaved()
    {
        var s = await CreateAsync();

        Assert.False(s.Imaging.SaveFitsCommand.CanExecute(null));
        Assert.False(s.Imaging.SavePngCommand.CanExecute(null));
        Assert.False(s.Imaging.SaveFits(Path.Combine(_folder, "none.fits")));
        Assert.Contains("no frame", s.Imaging.SaveStatusText);
        Assert.False(File.Exists(Path.Combine(_folder, "none.fits")));
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_IsReported_NotThrown()
    {
        var s = await CreateAsync();
        await s.Capture.CaptureCommand.ExecuteAsync(null);

        Assert.False(s.Imaging.SaveFits(Path.Combine(_folder, "no-such-folder", "x.fits")));
        Assert.Contains("could not be saved", s.Imaging.SaveStatusText);
    }

    [Fact]
    public async Task ThePixelSizeInTheFits_IsTheBinnedOne()
    {
        var s = await CreateAsync();
        var frame = new CameraFrame(4, 4, new ushort[16], TimeSpan.FromSeconds(1)) { Acquisition = new FrameAcquisition { BinX = 2, BinY = 2 } };

        var metadata = FrameExporter.MetadataFor(frame, s.Host, new DeviceId("camera.main"), null);

        Assert.Equal(3.76 * 2, metadata.PixelSizeXMicrons!.Value, 9);
        Assert.Equal(750, metadata.FocalLengthMm);
        Assert.Null(metadata.SiteLatitudeDegrees); // no site is configured: none is written
    }

    // ---- Manual autofocus

    [Fact]
    public async Task OnlyRigsWithAFocuser_CanBeFocused()
    {
        var s = await CreateAsync();

        Assert.Equal(["Main Rig", "Wide Rig"], s.Autofocus.Rigs.Select(r => r.Name));
        Assert.Equal("19500", s.Autofocus.PositionText);
    }

    [Fact]
    public async Task AutofocusRuns_ShowsTheSamplesLive_AndTheResult()
    {
        var s = await CreateAsync();

        await s.Autofocus.StartCommand.ExecuteAsync(null);

        Assert.False(s.Autofocus.HasError, s.Autofocus.ErrorText);
        Assert.Equal(7, s.Autofocus.Samples.Count);
        Assert.Equal(s.Autofocus.Samples.Select(x => x.Position).Distinct().Count(), s.Autofocus.Samples.Count);
        Assert.NotNull(s.Autofocus.BestPosition);
        Assert.InRange(s.Autofocus.BestPosition!.Value, 19000, 21000); // near where the simulated optics are in focus
        Assert.Equal(s.Autofocus.BestPosition.Value.ToString(), s.Autofocus.BestFocusText);
        Assert.EndsWith(" px", s.Autofocus.BestHfrText);
        Assert.Contains("Fitted HFR", s.Autofocus.FitText);
        Assert.StartsWith("Focused at", s.Autofocus.StatusText);
        Assert.True(s.Autofocus.HasResult);
        Assert.False(s.Autofocus.IsRunning);
        Assert.Equal(s.Autofocus.BestPosition.Value.ToString(), s.Autofocus.PositionText[..s.Autofocus.PositionText.Length]);
    }

    [Fact]
    public async Task AutofocusOfOneRig_LeavesTheOtherRigsFocuserAlone()
    {
        var s = await CreateAsync();
        var wide = (IFocuser)s.Host.DeviceRegistry.GetAll().Single(d => d.Id == new DeviceId("focuser.wide"));

        await s.Autofocus.StartCommand.ExecuteAsync(null);

        Assert.Equal(5500, wide.Position);
    }

    [Fact]
    public async Task AutofocusCanBeSelectedForAnotherRig()
    {
        var s = await CreateAsync();
        s.Autofocus.SelectedRig = s.Autofocus.Rigs.Single(r => r.Name == "Wide Rig");
        s.Autofocus.StepSizeText = "150";

        await s.Autofocus.StartCommand.ExecuteAsync(null);

        Assert.False(s.Autofocus.HasError, s.Autofocus.ErrorText);
        Assert.InRange(s.Autofocus.BestPosition!.Value, 5500, 6500);
    }

    [Fact]
    public async Task AutofocusCanBeCancelled_AndLeavesTheEquipmentFree()
    {
        var s = await CreateAsync();
        s.Autofocus.ExposureText = "30";

        var run = s.Autofocus.StartCommand.ExecuteAsync(null);
        await WaitAsync(() => s.Autofocus.IsRunning, "the run");
        Assert.False(s.Autofocus.StartCommand.CanExecute(null));
        Assert.True(s.Autofocus.CancelCommand.CanExecute(null));
        s.Autofocus.CancelCommand.Execute(null);
        await run;

        Assert.Equal("Cancelled.", s.Autofocus.StatusText);
        Assert.False(s.Autofocus.IsRunning);
        Assert.Null(s.Autofocus.BestPosition);
        using var lease = await s.Host.ResourceManager.AcquireAsync(
            [ResourceId.ForDevice(new("camera.main")), ResourceId.ForDevice(new("focuser.main"))], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AutofocusWillNotStart_WhileTheCameraOrFocuserIsInUse_OrNotConnected()
    {
        var s = await CreateAsync();
        var held = await s.Host.ResourceManager.AcquireAsync([ResourceId.ForDevice(new("camera.main"))], CancellationToken.None);
        s.Autofocus.Refresh();

        Assert.False(s.Autofocus.StartCommand.CanExecute(null));
        Assert.Contains("in use", s.Autofocus.DisabledText);
        held.Dispose();
        s.Autofocus.Refresh();
        Assert.True(s.Autofocus.StartCommand.CanExecute(null));

        await s.Host.DeviceRegistry.GetAll().Single(d => d.Id == new DeviceId("focuser.main")).DisconnectAsync();
        s.Autofocus.Refresh();
        Assert.False(s.Autofocus.StartCommand.CanExecute(null));
        Assert.Contains("Connect the focuser", s.Autofocus.DisabledText);
    }

    [Theory]
    [InlineData("0", "300", "7")]
    [InlineData("1", "0", "7")]
    [InlineData("1", "300", "3")]
    [InlineData("1", "300", "99")]
    [InlineData("x", "300", "7")]
    public async Task NonsensicalAutofocusSettings_AreRefused(string exposure, string step, string samples)
    {
        var s = await CreateAsync();
        s.Autofocus.ExposureText = exposure;
        s.Autofocus.StepSizeText = step;
        s.Autofocus.SamplesText = samples;

        Assert.False(s.Autofocus.TryBuildOptions(out _, out var problem));
        Assert.NotNull(problem);
        await s.Autofocus.StartCommand.ExecuteAsync(null);
        Assert.True(s.Autofocus.HasError);
        Assert.Empty(s.Autofocus.Samples);
    }

    [Fact]
    public async Task WithoutAFocuserOnAnyRig_AutofocusSaysSo()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedCamera(new("camera.only"), "Only Camera", 1);
        host.AddRig(new Rig(new("rig.only"), "Only Rig", new("camera.only")));
        var autofocus = new ManualAutofocusViewModel(host, SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry));

        Assert.False(autofocus.HasRig);
        Assert.Contains("No rig has a focuser", autofocus.DisabledText);
        Assert.False(autofocus.StartCommand.CanExecute(null));
        await Task.CompletedTask;
    }
}
