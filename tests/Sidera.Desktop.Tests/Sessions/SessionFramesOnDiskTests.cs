using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Imaging;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.Settings;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>A session that runs on the simulator leaves its frames on disk: one FITS file for every exposure, in the folders and under the names of the settings, with the target in the header.</summary>
public sealed class SessionFramesOnDiskTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidera-session-frames-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static SessionDefinition Session(string target, int frames, params int?[] filters)
    {
        var lanes = filters.Select(f => Lane(new Sidera.Core.Rigs.RigId("rig.main"), Block(f, 0.05, frames))).Take(1).ToList();
        var session = new SessionTarget(Guid.NewGuid(), target, 5.58, -5.4, null, true, [], lanes, []);
        return new SessionDefinition(new SessionAutomation(new Sidera.Core.Mounts.MeridianFlipSettings()), [], [session], []);
    }

    private static async Task<(SideraRuntimeHost Host, SessionFixture Fixture)> ConnectedAsync()
    {
        var fixture = Create();
        foreach (var device in fixture.Host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        return (fixture.Host, fixture);
    }

    private async Task<List<string>> RunAsync(SideraRuntimeHost host, SessionDefinition definition, ImagingSettings settings)
    {
        await using var saver = new SessionFrameSaver(host, () => settings, () => null);
        var compiled = SessionCompiler.Compile(definition, new Sidera.Runtime.Rigs.ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry));
        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);
        var context = new SequenceDraftContext(host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving, null, null, FrameSink: saver);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, compiled.Steps, context);
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);

        await runner.RunAsync(built.Sequence).WaitAsync(TimeSpan.FromSeconds(60));
        await saver.FlushAsync();

        Assert.Equal(SequenceState.Completed, runner.State);
        return Directory.Exists(_root) ? Directory.GetFiles(_root, "*.fits", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f)).Order().ToList() : [];
    }

    [Fact]
    public async Task EveryExposureOfARun_IsOneFileNamedAfterTheTargetTheFilterAndTheNumber()
    {
        var (host, fixture) = await ConnectedAsync();
        await using var _ = fixture;
        var night = DateTime.Now.AddHours(-12).ToString("yyyy-MM-dd");

        var files = await RunAsync(host, Session("M42", 3, 1), new ImagingSettings { SaveDirectory = _root });

        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.StartsWith(Path.Combine("M42", night, "Light") + Path.DirectorySeparatorChar, f));
        Assert.Equal(["M42_Light_R_0.05s_0001.fits", "M42_Light_R_0.05s_0002.fits", "M42_Light_R_0.05s_0003.fits"], files.Select(Path.GetFileName));
        var header = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_root, files[0])), 0, 2880);
        Assert.Contains("OBJECT  = 'M42", header);
        Assert.Contains("FILTER  = 'R", header);
    }

    [Fact]
    public async Task WithSavingOff_ARunLeavesNothingOnDisk()
    {
        var (host, fixture) = await ConnectedAsync();
        await using var _ = fixture;

        var files = await RunAsync(host, Session("M42", 2, 1), new ImagingSettings { SaveDirectory = _root, SaveSessionFrames = false });

        Assert.Empty(files);
    }

    [Fact]
    public async Task ATwoWordTarget_MakesOneFolder_AndTheFileNamesStayReadable()
    {
        var (host, fixture) = await ConnectedAsync();
        await using var _ = fixture;

        var files = await RunAsync(host, Session("NGC 7000/North America", 1, 2), new ImagingSettings { SaveDirectory = _root, FolderPattern = "{target}" });

        Assert.Equal(["NGC_7000-North_America" + Path.DirectorySeparatorChar + "NGC_7000-North_America_Light_G_0.05s_0001.fits"], files);
    }
}
