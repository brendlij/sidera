using Astra.Core.Devices;
using Astra.Core.Rigs;
using Astra.Desktop.Diagnostics;
using Astra.Desktop.ViewModels;
using Astra.Runtime;

namespace Astra.Desktop.Tests.Ux;

/// <summary>How much of the equipment an application of the tests is composed with.</summary>
public enum UxSetup
{
    /// <summary>One camera, a mount and a guider. No focuser, no filter wheel, no rig at all.</summary>
    Simple,

    /// <summary>The demo of one rig: its camera, focuser and filter wheel, with the mount and the guider.</summary>
    OneRig,

    /// <summary>The demo of three rigs (the wide one has no filter wheel) on one mount and one guider.</summary>
    Demo,
}

/// <summary>A composed Astra (runtime host and shell view model) with short timings, for the tests of the user interface.</summary>
internal sealed class UxApp : IAsyncDisposable
{
    private static readonly DemoOptions Fast = new()
    {
        ManualExposure = TimeSpan.FromMilliseconds(100),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(60),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
        FocuserStepsPerSecond = 20000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(30),
    };

    private UxApp(AstraRuntimeHost host, MainViewModel vm)
    {
        Host = host;
        Vm = vm;
    }

    public AstraRuntimeHost Host { get; }
    public MainViewModel Vm { get; }

    public static async Task<UxApp> Create(
        UxSetup setup, bool connect = false, LogInfo? logInfo = null, IClipboardService? clipboard = null, IFolderOpener? opener = null)
    {
        var host = new AstraRuntimeHost();
        switch (setup)
        {
            case UxSetup.Simple:
                host.AddSimulatedCamera(DemoSetup.MainCameraId, "Main Camera");
                host.AddSimulatedMount(DemoSetup.MountId, "EQ6 Mount", Fast.SlewDuration);
                host.AddSimulatedGuider(
                    DemoSetup.GuiderId, "Main Guider", Fast.GuiderStartDuration, Fast.GuiderStopDuration, Fast.GuiderDitherDuration);
                break;
            case UxSetup.OneRig:
                DemoSetup.AddDemoEquipment(host, Fast);
                break;
            default:
                DemoSetup.AddDemoEquipment(host, Fast);
                DemoSetup.AddDemoRigs(host, Fast);
                break;
        }

        if (connect)
        {
            foreach (var device in host.DeviceRegistry.GetAll())
            {
                await device.ConnectAsync();
            }
        }

        var gate = new object();
        var vm = new MainViewModel(
            host, action => { lock (gate) { action(); } }, Fast, logInfo: logInfo, clipboard: clipboard, folderOpener: opener);
        return new UxApp(host, vm);
    }

    public async ValueTask DisposeAsync()
    {
        Vm.Dispose();
        await Host.DisposeAsync();
    }

    public static async Task WaitUntil(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    public RigId Rig(string id) => new(id);
}
