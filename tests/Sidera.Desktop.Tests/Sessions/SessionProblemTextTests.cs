using Sidera.Core.Devices;
using Sidera.Desktop.ViewModels;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>How the problems of a session are shown: short, and each one where it belongs, not four times.</summary>
public sealed class SessionProblemTextTests : IAsyncLifetime
{
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }
    }

    private async Task<(SideraRuntimeHost Host, SessionEditorViewModel Editor)> TwoCamerasAsync()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new("camera.a"), "Camera a", 1);
        host.AddSimulatedCamera(new("camera.b"), "Camera b", 2);
        foreach (var id in new[] { "camera.a", "camera.b" })
        {
            host.DeviceRegistry.TryGet(new DeviceId(id), out var device);
            await device!.ConnectAsync();
        }

        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add((host, vm));
        return (host, vm.SessionEditor);
    }

    private static SessionDefinition LaneWithoutASetup() => SessionFixture.Session(SessionFixture.Target("M31", [SessionFixture.Lane(null, SessionFixture.Block(null, 60, 5))]));

    [Fact]
    public async Task AProblemOfASequence_IsShortAndSaidAtTheSequence_NotAgainInTheBanner()
    {
        var (_, editor) = await TwoCamerasAsync();
        editor.Load(LaneWithoutASetup());

        var problem = Assert.Single(editor.Problems);
        Assert.Contains("No imaging setup.", problem, StringComparison.Ordinal);
        Assert.True(problem.Length < 190, problem);
        Assert.Contains("No imaging setup.", editor.Targets[0].Lanes[0].ProblemText, StringComparison.Ordinal);
        Assert.Empty(editor.BannerProblems); // the sequence says it; the banner is for what no card shows
        Assert.False(editor.HasBannerProblems);
        Assert.Equal("1 problem to fix.", editor.CanRunText); // the status counts, it does not repeat
    }

    [Fact]
    public async Task AProblemOfNoCard_IsInTheBanner_AndCounted()
    {
        var (host, editor) = await TwoCamerasAsync();
        host.AddRig(new Rig(new RigId("rig.a"), "Camera a", new DeviceId("camera.a"), new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176)));
        var lane = SessionFixture.Lane(new RigId("rig.a"), SessionFixture.Block(null, 60, 5));
        editor.Load(SessionFixture.Session(SessionFixture.Target("M31", [lane])) with
        {
            Automation = new SessionAutomation(new MeridianFlipSettings { Enabled = true, FlipAfterMeridianMinutes = 20, LatestAllowedFlipMinutes = 10 }),
        });

        Assert.True(editor.HasBannerProblems);
        Assert.All(editor.BannerProblems, p => Assert.StartsWith("Meridian flip: ", p, StringComparison.Ordinal)); // about the session, not about a card
        const string noMount = "needs a setup with a mount";
        Assert.Contains(editor.Problems, p => p.Contains(noMount, StringComparison.Ordinal)); // the sequence says that its setup has no mount to flip
        Assert.DoesNotContain(editor.BannerProblems, p => p.Contains(noMount, StringComparison.Ordinal));
        Assert.Equal($"{editor.Problems.Count} problems to fix.", editor.CanRunText);
    }

    [Fact]
    public async Task TheLineOfABlock_SaysNothingOfDither_WhileDitherIsOff_BecauseTheSwitchSaysIt()
    {
        var (_, editor) = await TwoCamerasAsync();
        editor.Load(LaneWithoutASetup());
        editor.SelectBlock(editor.Session!.Targets[0].Lanes[0].Blocks[0].Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);

        Assert.DoesNotContain("Dither", editor.Targets[0].Lanes[0].Blocks[0].Line, StringComparison.Ordinal);

        drawer.DitherOn = true;

        Assert.Contains("Dither 3", editor.Targets[0].Lanes[0].Blocks[0].Line, StringComparison.Ordinal);
    }
}
