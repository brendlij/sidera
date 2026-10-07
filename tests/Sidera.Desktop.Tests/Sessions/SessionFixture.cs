using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>Equipment for the tests of sessions: a main setup (camera, focuser, filter wheel L R G B, mount, guider) and, when asked, a second one that shares the mount and the guider or has its own.</summary>
public sealed class SessionFixture : IAsyncDisposable
{
    public static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    public static readonly DeviceId CameraA = new("camera.a");
    public static readonly DeviceId CameraB = new("camera.b");
    public static readonly ImagingBindingId MainPath = ImagingBindingId.For(CameraA);
    public static readonly ImagingBindingId WidePath = ImagingBindingId.For(CameraB);

    public SideraRuntimeHost Host { get; } = new();

    public ImagingSetupCatalog Catalog => new(Host.DeviceRegistry, Host.RigRegistry);

    public static SessionFixture Create(bool second = false, bool sharedMount = true, bool withRotator = false, bool explicitMain = true, bool mainHasGuider = true, bool mainHasFocuser = true)
    {
        var f = new SessionFixture();
        var host = f.Host;
        host.AddSimulatedCamera(CameraA, "Camera A", 1);
        host.AddSimulatedFocuser(new("focuser.a"), "Focuser A", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedFilterWheel(new("wheel.a"), "Wheel A", [new FilterSlot(0, "L"), new FilterSlot(1, "R"), new FilterSlot(2, "G"), new FilterSlot(3, "B")]);
        host.AddSimulatedMount(new("mount.1"), "AM3", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.1"), "PHD2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        if (withRotator)
        {
            host.AddSimulatedRotator(new("rotator.a"), "Rotator A");
        }

        if (explicitMain)
        {
            host.AddRig(new Rig(
                new RigId("rig.main"), "Main 750", CameraA, Optics, mainHasFocuser ? new DeviceId("focuser.a") : null, new DeviceId("wheel.a"), withRotator ? new DeviceId("rotator.a") : null, null,
                new DeviceId("mount.1"), mainHasGuider ? new DeviceId("guider.1") : null));
        }

        if (second)
        {
            host.AddSimulatedCamera(CameraB, "Camera B", 2);
            host.AddSimulatedFocuser(new("focuser.b"), "Focuser B", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
            if (!sharedMount)
            {
                host.AddSimulatedMount(new("mount.2"), "EQ6", TimeSpan.FromMilliseconds(20));
                host.AddSimulatedGuider(new("guider.2"), "Guider 2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
            }

            host.AddRig(new Rig(
                new RigId("rig.wide"), "Wide 400", CameraB, Optics, new DeviceId("focuser.b"), null, null, null, new DeviceId(sharedMount ? "mount.1" : "mount.2"),
                new DeviceId(sharedMount ? "guider.1" : "guider.2")));
        }

        return f;
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();

    // ---- a session in a few words

    public static SequenceBlock Block(int? filter, double seconds, int frames, Action<Builder>? configure = null)
    {
        var block = SequenceBlock.Imaging(null, filter, seconds, frames);
        if (configure is null)
        {
            return block;
        }

        var builder = new Builder(block);
        configure(builder);
        return builder.Block;
    }

    public sealed class Builder(SequenceBlock block)
    {
        public SequenceBlock Block { get; private set; } = block;

        public Builder Dither(int every) => Set(b => b with { Automation = b.Automation with { Dither = new DitherAutomation(every, DitherSettings.Default) } });

        public Builder Dither(int every, DitherSettings settings) => Set(b => b with { Automation = b.Automation with { Dither = new DitherAutomation(every, settings) } });

        public Builder Focus(bool atStart = false, double everyMinutes = 0, bool afterFilter = false, FocusSettings? settings = null) =>
            Set(b => b with { Automation = b.Automation with { Focus = new FocusAutomation(atStart, everyMinutes, afterFilter, settings ?? FocusSettings.Default) } });

        public Builder Limits(params Sidera.Core.Conditions.WorkflowCondition[] limits) => Set(b => b with { Limits = limits });

        public Builder Repeat(RepeatRule rule) => Set(b => b with { Repeat = rule });

        public Builder Actions(params SessionAction[] actions) => Set(b => b with { Actions = actions });

        public Builder Disabled() => Set(b => b with { Enabled = false });

        private Builder Set(Func<SequenceBlock, SequenceBlock> change)
        {
            Block = change(Block);
            return this;
        }
    }

    public static SetupLane Lane(ImagingBindingId? setup, params SequenceBlock[] blocks) => new(Guid.NewGuid(), setup, blocks);

    public static SessionTarget Target(string name, IReadOnlyList<SetupLane> lanes, IReadOnlyList<SessionAction>? preparation = null, double? rotation = null) =>
        new(Guid.NewGuid(), name, 5.588, -5.39, rotation, true, preparation ?? [], lanes, []);

    public static SessionDefinition Session(params SessionTarget[] targets) => SessionDefinition.Empty with { Targets = targets };
}
