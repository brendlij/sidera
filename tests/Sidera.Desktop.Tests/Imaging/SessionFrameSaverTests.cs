using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Imaging;
using Sidera.Core.Rigs;
using Sidera.Desktop.Imaging;
using Sidera.Desktop.Settings;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Imaging;

/// <summary>The frames of a session on disk: where they go, what they are called, what their header says, and that a problem is told and never loses the exposure.</summary>
public sealed class SessionFrameSaverTests : IAsyncLifetime
{
    private static readonly DeviceId CameraId = new("camera.main");
    private static readonly DateTime Start = new(2026, 10, 13, 1, 45, 30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidera-frames-" + Guid.NewGuid().ToString("N"));
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly List<SessionFrameSaver> _savers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var saver in _savers)
        {
            await saver.DisposeAsync();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<SideraRuntimeHost> CreateHostAsync(bool withWheel = true)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedCamera(CameraId, "Camera A", 1);
        if (withWheel)
        {
            var wheel = host.AddSimulatedFilterWheel(new("wheel.main"), "Wheel", [new FilterSlot(0, "L"), new FilterSlot(1, "Ha")], moveDuration: TimeSpan.FromMilliseconds(5));
            host.AddRig(new Rig(new("rig.main"), "Newton", CameraId, new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176), null, new("wheel.main")));
            await host.DeviceOperations.ConnectAsync(wheel.Id);
            await wheel.MoveToSlotAsync(1);
        }

        await host.DeviceOperations.ConnectAsync(CameraId);
        return host;
    }

    private SessionFrameSaver Saver(SideraRuntimeHost host, ImagingSettings? settings = null, List<(string Title, string Message)>? problems = null)
    {
        settings ??= new ImagingSettings { SaveDirectory = _root };
        var saver = new SessionFrameSaver(host, () => settings, () => null, (title, message) => problems?.Add((title, message)), now: () => Start);
        _savers.Add(saver);
        return saver;
    }

    private static CameraFrame Frame(double seconds = 2) => new(8, 6, new ushort[48], TimeSpan.FromSeconds(seconds))
    {
        Acquisition = new FrameAcquisition { FrameType = FrameType.Light, Gain = 100, Offset = 50, BinX = 1, BinY = 1 },
    };

    private static async Task SaveAsync(SessionFrameSaver saver, string? target = "M31", CameraFrame? frame = null)
    {
        saver.Begin(CameraId, new FrameLabel(target)).Complete(frame ?? Frame());
        await saver.FlushAsync();
    }

    private static string Header(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Encoding.ASCII.GetString(bytes, 0, 2880);
    }

    [Fact]
    public async Task AFrame_IsSavedAsFits_InTheFoldersOfTheTargetTheNightAndTheType_UnderTheDefaultName()
    {
        var saver = Saver(await CreateHostAsync());

        await SaveAsync(saver);

        var expected = Path.Combine(_root, "M31", "2026-10-12", "Light", "M31_Light_Ha_2s_0001.fits");
        Assert.True(File.Exists(expected), expected);
        Assert.Equal(expected, saver.LastSavedPath);
        Assert.Equal(0, saver.Pending);
    }

    [Fact]
    public async Task TheHeader_NamesTheTargetTheFilterTheCameraAndWhenTheExposureStarted()
    {
        var saver = Saver(await CreateHostAsync());

        await SaveAsync(saver);

        var header = Header(saver.LastSavedPath!);
        Assert.Contains("OBJECT  = 'M31", header);
        Assert.Contains("FILTER  = 'Ha", header);
        Assert.Contains("INSTRUME= 'Camera A", header);
        Assert.Contains("TELESCOP= 'Newton", header);
        Assert.Contains("EXPTIME", header);
        Assert.Contains("GAIN", header);
        Assert.Contains("DATE-OBS", header);
        Assert.Contains("IMAGETYP= 'Light Frame", header);
    }

    [Fact]
    public async Task AFilterThatIsNotKnown_IsLeftOutOfTheNameAndTheHeader_AndNothingIsMadeUp()
    {
        var saver = Saver(await CreateHostAsync(withWheel: false));

        await SaveAsync(saver, target: null);

        // No target and no filter: the folders are the night and the type; the name is the type and the exposure.
        Assert.Equal(Path.Combine(_root, "2026-10-12", "Light", "Light_2s_0001.fits"), saver.LastSavedPath);
        var header = Header(saver.LastSavedPath!);
        Assert.DoesNotContain("OBJECT", header);
        Assert.DoesNotContain("FILTER", header);
    }

    [Fact]
    public async Task TheNumberCountsUp_AndAFileThatIsThereIsNeverReplaced()
    {
        var host = await CreateHostAsync();
        var folder = Path.Combine(_root, "M31", "2026-10-12", "Light");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "M31_Light_Ha_2s_0001.fits");
        File.WriteAllText(old, "from an earlier night");
        var saver = Saver(host);

        await SaveAsync(saver);
        await SaveAsync(saver);

        Assert.Equal("from an earlier night", File.ReadAllText(old));
        Assert.Equal(["M31_Light_Ha_2s_0001.fits", "M31_Light_Ha_2s_0002.fits", "M31_Light_Ha_2s_0003.fits"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task NoFileIsLeftHalfWritten_OnlyCompleteFramesWithTheirOwnName()
    {
        var saver = Saver(await CreateHostAsync());

        await SaveAsync(saver);

        Assert.Empty(Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories));
        var path = saver.LastSavedPath!;
        Assert.Equal(0, new FileInfo(path).Length % 2880);
        Assert.True(new FileInfo(path).Length >= 2880 + 8 * 6 * 2);
    }

    [Fact]
    public async Task WhenSavingIsOff_NothingIsWritten()
    {
        var saver = Saver(await CreateHostAsync(), new ImagingSettings { SaveDirectory = _root, SaveSessionFrames = false });

        await SaveAsync(saver);

        Assert.False(Directory.Exists(_root));
        Assert.Null(saver.LastSavedPath);
    }

    [Fact]
    public async Task ThePatternsOfTheSettings_DecideTheFoldersAndTheName()
    {
        var settings = new ImagingSettings { SaveDirectory = _root, FolderPattern = "{night}/{filter}", FilePattern = "{target}-{type}-{exposure}-{gain}-{n}" };
        var saver = Saver(await CreateHostAsync(), settings);

        await SaveAsync(saver);

        Assert.Equal(Path.Combine(_root, "2026-10-12", "Ha", "M31-Light-2s-gain100-0001.fits"), saver.LastSavedPath);
    }

    [Fact]
    public async Task AFolderPatternThatClimbs_StaysInsideTheSaveFolder()
    {
        var settings = new ImagingSettings { SaveDirectory = _root, FolderPattern = "../../{target}" };
        var saver = Saver(await CreateHostAsync(), settings);

        await SaveAsync(saver);

        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(saver.LastSavedPath!));
        Assert.Equal(Path.Combine(_root, "M31"), Path.GetDirectoryName(saver.LastSavedPath));
    }

    [Fact]
    public async Task AFrameThatCannotBeWritten_IsToldToTheUserOnce_AndTheNextOneStillTries()
    {
        var problems = new List<(string Title, string Message)>();
        var blocked = Path.Combine(Path.GetTempPath(), "sidera-frames-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "a file where the save folder should be");
        try
        {
            var saver = Saver(await CreateHostAsync(), new ImagingSettings { SaveDirectory = blocked }, problems);

            await SaveAsync(saver);
            await SaveAsync(saver);

            var problem = Assert.Single(problems);
            Assert.Equal("A frame was not saved", problem.Title);
            Assert.Contains("Settings", problem.Message);
            Assert.Null(saver.LastSavedPath);
        }
        finally
        {
            File.Delete(blocked);
        }
    }

    [Fact]
    public async Task AFrameThatIsAbandoned_WritesNothing()
    {
        var saver = Saver(await CreateHostAsync());

        saver.Begin(CameraId, new FrameLabel("M31")).Abandon();
        await saver.FlushAsync();

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task TheSettingsOfTheMomentTheExposureStarted_DecideTheFile_NotThoseOfWhenItEnds()
    {
        var settings = new ImagingSettings { SaveDirectory = _root };
        var host = await CreateHostAsync();
        var saver = new SessionFrameSaver(host, () => settings, () => null, now: () => Start);
        _savers.Add(saver);

        var capture = saver.Begin(CameraId, new FrameLabel("M31"));
        settings = settings with { SaveSessionFrames = false };
        capture.Complete(Frame());
        await saver.FlushAsync();

        Assert.NotNull(saver.LastSavedPath);
    }

    [Fact]
    public void TheSettings_RoundTrip_WithTheirPatterns_AndAnOldFileGetsTheDefaults()
    {
        var settings = SideraSettings.Empty with
        {
            Imaging = new ImagingSettings { SaveDirectory = @"D:\Astro", SaveSessionFrames = false, FolderPattern = "{night}/{target}", FilePattern = "{target}_{n}" },
        };

        var loaded = SideraSettingsSerializer.Deserialize(SideraSettingsSerializer.Serialize(settings)).Imaging;

        Assert.Equal(settings.Imaging, loaded);
        var old = SideraSettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"format\":\"sidera-settings\",\"version\":1,\"imaging\":{\"autoStretch\":true}}")).Imaging;
        Assert.True(old.SaveSessionFrames);
        Assert.Equal(FrameNaming.DefaultFolderPattern, old.FolderPattern);
        Assert.Equal(FrameNaming.DefaultFilePattern, old.FilePattern);
    }

    [Fact]
    public void ASettingWithAPatternThatCannotBeUsed_IsRefused_WithTheReason()
    {
        Assert.Contains("unknown part {nope}", new ImagingSettings { FilePattern = "{nope}" }.Problem);
        Assert.Contains("inside the save folder", new ImagingSettings { FolderPattern = "C:/x" }.Problem);
        Assert.Null(new ImagingSettings().Problem);
    }

    [Fact]
    public void WithoutASaveFolder_FramesGoToTheSideraFolderInPictures()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sidera"), new ImagingSettings().FramesRoot);
        Assert.Equal(@"D:\Astro", new ImagingSettings { SaveDirectory = @"D:\Astro" }.FramesRoot);
    }
}
