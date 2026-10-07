using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.Tests.Sessions;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>The bar at the bottom of the window says what Sidera is doing, on every page.</summary>
public sealed class StatusBarTests : IAsyncLifetime
{
    private SideraRuntimeHost? _host;
    private MainViewModel? _vm;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _vm?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheBar_SaysWhatIsRunning_WhileItRuns_AndHowTheRunEnded()
    {
        _host = new SideraRuntimeHost();
        _host.AddSimulatedCamera(new("camera.only"), "Only camera", 1);
        _host.DeviceRegistry.TryGet(new DeviceId("camera.only"), out var camera);
        await camera!.ConnectAsync();
        _vm = new MainViewModel(_host, a => a(), new DemoOptions());
        var bar = _vm.StatusBar;
        _vm.SessionEditor.Load(SessionFixture.Session(SessionFixture.Target("M31", [SessionFixture.Lane(null, SessionFixture.Block(null, 0.2, 4))])));

        Assert.Equal("Idle", bar.StateText);
        Assert.Equal("Ready.", bar.ActivityText);
        Assert.False(bar.HasElapsed);

        _vm.Sequencer.RunCommand.Execute(null);
        var sawExposure = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (_vm.Sequencer.State is SequenceState.Running or SequenceState.Idle)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sequence.");
            sawExposure |= bar.StateText == "Running" && bar.ActivityText.Contains("Exposure", StringComparison.Ordinal);
            await Task.Delay(5);
        }

        Assert.True(sawExposure, "while it runs the bar names the step that is at work");
        Assert.Equal("Completed", bar.StateText);
        Assert.Equal("The sequence finished.", bar.ActivityText);
        Assert.True(bar.HasElapsed);
    }

    [Fact]
    public async Task WhenTheSequenceCannotStart_TheBarSaysWhy()
    {
        _host = new SideraRuntimeHost();
        _host.AddSimulatedCamera(new("camera.only"), "Only camera", 1);
        _vm = new MainViewModel(_host, a => a(), new DemoOptions());
        _vm.SessionEditor.Load(SessionFixture.Session(SessionFixture.Target("M31", [SessionFixture.Lane(null, SessionFixture.Block(null, 0.2, 4))])));

        Assert.Equal(_vm.Sequencer.ReadinessHint ?? "Ready.", _vm.StatusBar.ActivityText);
        Assert.NotEqual("Ready.", _vm.StatusBar.ActivityText); // the camera is not connected: the bar says so
    }
}
