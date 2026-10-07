using Sidera.Core.Devices;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>How the problems of a workflow are shown: short, and each one where it belongs, not four times.</summary>
public sealed class WorkflowProblemTextTests : IAsyncLifetime
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

    private async Task<(SideraRuntimeHost Host, WorkflowEditorViewModel Editor)> TwoCamerasAsync()
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
        return (host, vm.Workflow);
    }

    [Fact]
    public async Task AProblemOfARow_IsShortAndSaidAtTheRow_NotAgainInTheBanner()
    {
        var (_, editor) = await TwoCamerasAsync();
        editor.Load(WorkflowDefinition.Empty with { Imaging = [new ImagingBlock(Guid.NewGuid(), null, null, 60, 5)] });

        var problem = Assert.Single(editor.Problems);
        Assert.StartsWith("Imaging block 1: No imaging setup.", problem, StringComparison.Ordinal);
        Assert.True(problem.Length < 140, problem);
        Assert.Contains("No imaging setup.", editor.ImagingRows[0].ProblemText, StringComparison.Ordinal);
        Assert.Empty(editor.BannerProblems); // the row says it; the banner is for what no row shows
        Assert.False(editor.HasBannerProblems);
        Assert.Equal("1 problem to fix.", editor.CanRunText); // the status counts, it does not repeat
    }

    [Fact]
    public async Task AProblemOfNoRow_IsInTheBanner_AndCounted()
    {
        var (_, editor) = await TwoCamerasAsync();
        editor.Load(WorkflowDefinition.Empty with { Imaging = [new ImagingBlock(Guid.NewGuid(), null, null, 60, 5)] });

        editor.DitherEnabled = true;
        editor.DitherEveryText = "often";

        Assert.True(editor.HasBannerProblems);
        var banner = Assert.Single(editor.BannerProblems);
        Assert.Contains("Dither every N frames", banner, StringComparison.Ordinal);
        Assert.Equal("2 problems to fix.", editor.CanRunText);
    }

    [Fact]
    public async Task TheDitherSummary_SaysNothingWhileDitherIsOff_BecauseTheCheckboxSaysIt()
    {
        var (_, editor) = await TwoCamerasAsync();
        editor.Load(WorkflowDefinition.Empty with { Imaging = [new ImagingBlock(Guid.NewGuid(), null, null, 60, 5)] });

        Assert.Equal(string.Empty, editor.DitherSummary);

        editor.DitherEnabled = true;

        Assert.Contains("Every 3 frames", editor.DitherSummary, StringComparison.Ordinal);
    }
}
