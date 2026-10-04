using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Core.Focusing;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using Astra.Runtime.Sequencing;
using Astra.Runtime.Tests.Sequencing;

namespace Astra.Runtime.Tests.Focusers;

/// <summary>
/// A relative focuser has no position. Everything that needs one refuses it before anything moves; no coordinate is made up.
/// </summary>
public class RelativeFocuserTests
{
    private static readonly DeviceId FocuserId = new("focuser.relative");
    private static readonly DeviceId CameraId = new("camera.main");

    private sealed class RelativeFocuser : IFocuser
    {
        public int MoveCalls { get; private set; }

        public DeviceId Id => FocuserId;
        public string Name => "Relative focuser";
        public DeviceType Type => DeviceType.Focuser;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public bool IsAbsolute => false;
        public FocuserMotionState MotionState => FocuserMotionState.Idle;
        public int Position => 0;
        public int MinPosition => 0;
        public int MaxPosition => 0;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MoveToAsync(int target, CancellationToken cancellationToken = default)
        {
            MoveCalls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AMoveToAPosition_IsRefusedBeforeTheFocuserIsTouched()
    {
        await using var host = new AstraRuntimeHost();
        var focuser = new RelativeFocuser();
        host.AddDevice(focuser);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.MoveFocuserToAsync(FocuserId, 100));

        Assert.Contains("relative focuser", failure.Message);
        Assert.Equal(0, focuser.MoveCalls);
    }

    [Fact]
    public async Task Autofocus_RefusesARelativeFocuser_AndNothingMoves()
    {
        await using var host = new AstraRuntimeHost();
        var focuser = new RelativeFocuser();
        host.AddDevice(focuser);
        host.AddSimulatedCamera(CameraId, "Camera", seed: 1);
        var rig = new Rig(new RigId("rig.main"), "Main", CameraId, new OpticalTrain(750, 150, 3.76, 23.5, 15.7, 6248, 4176), FocuserId);
        host.AddRig(rig);
        await host.DeviceRegistry.GetAll().First(d => d.Id == CameraId).ConnectAsync();
        var action = AutofocusAction.ForRig(
            host.DeviceRegistry, rig, new AutofocusOptions(TimeSpan.FromMilliseconds(40), 300, 7), host.FocusMetrics, host.EventBus);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => action.ExecuteAsync(NoContext.Instance, CancellationToken.None));

        Assert.Contains("relative focuser", failure.Message);
        Assert.Equal(0, focuser.MoveCalls);
    }
}
