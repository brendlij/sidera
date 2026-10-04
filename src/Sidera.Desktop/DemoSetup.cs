using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;

namespace Sidera.Desktop;

/// <summary>The simulated equipment of the demo, all registered with the host and none of it connected.</summary>
public sealed record DemoEquipment(
    SimulatedCamera Camera,
    SimulatedMount Mount,
    SimulatedGuider Guider,
    Rig Rig
);

/// <summary>
/// Composes the local demo: a camera, a focuser, a filter wheel, a mount and a guider, and the logical rig around the
/// camera, focuser and filter wheel. The mount and the guider are shared equipment, deliberately not part of the rig.
/// </summary>
public static class DemoSetup
{
    public static readonly RigId MainRigId = new("rig.main");
    public static readonly DeviceId MainCameraId = new("camera.main");
    public static readonly DeviceId MainFocuserId = new("focuser.main");
    public static readonly DeviceId MainFilterWheelId = new("filterwheel.main");
    public static readonly DeviceId MountId = new("mount.eq6");
    public static readonly DeviceId GuiderId = new("guider.main");

    public static readonly RigId WideRigId = new("rig.wide");
    public static readonly DeviceId WideCameraId = new("camera.wide");
    public static readonly DeviceId WideFocuserId = new("focuser.wide");
    public static readonly RigId NarrowRigId = new("rig.narrow");
    public static readonly DeviceId NarrowCameraId = new("camera.narrow");
    public static readonly DeviceId NarrowFocuserId = new("focuser.narrow");
    public static readonly DeviceId NarrowFilterWheelId = new("filterwheel.narrow");

    /// <summary>
    /// Where each demo rig is really in focus, in focuser steps. The focusers start away from it (main at 18200, wide at
    /// 5200, narrow at 24300), so that autofocus has something to do.
    /// </summary>
    public const int MainBestFocus = 20000;
    public const int WideBestFocus = 6000;
    public const int NarrowBestFocus = 25000;

    /// <summary>The coordination group of the demo sequence's parallel branches.</summary>
    public static readonly CoordinationGroupId CoordinationGroup = new("session.demo");

    /// <summary>The slots of the main filter wheel: a mono camera with LRGB and narrowband filters.</summary>
    public static IReadOnlyList<FilterSlot> MainFilters { get; } = Slots("L", "R", "G", "B", "Ha", "OIII", "SII");

    /// <summary>The slots of the narrow field rig's filter wheel.</summary>
    public static IReadOnlyList<FilterSlot> NarrowFilters { get; } = Slots("L", "Ha", "OIII", "SII");

    private static List<FilterSlot> Slots(params string[] names) =>
        names.Select((name, index) => new FilterSlot(index, name)).ToList();

    /// <summary>
    /// Adds a wide and a narrow field rig to the demo, each with a simulated camera and focuser of its own (the narrow
    /// field rig also has a filter wheel; the wide field rig has none): with the main rig a session of three
    /// telescopes on one mount and one guider, which is what Multi-Rig Imaging is for. The mount and the guider of
    /// <see cref="AddDemoEquipment"/> stay the shared equipment. Nothing is connected.
    /// </summary>
    public static void AddDemoRigs(SideraRuntimeHost host, DemoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new DemoOptions();

        var wide = host.AddSimulatedCamera(WideCameraId, "Wide Camera");
        var wideFocuser = AddFocuser(host, options, WideFocuserId, "Wide Focuser", start: 5200, max: 12000);
        host.AddRig(new Rig(
            WideRigId, "Wide Rig", wide.Id,
            new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176),
            wideFocuser.Id));
        host.AddSimulatedFocusModel(WideRigId, new SimulatedFocusModel(WideBestFocus));

        var narrow = host.AddSimulatedCamera(NarrowCameraId, "Narrow Camera");
        var narrowFocuser = AddFocuser(host, options, NarrowFocuserId, "Narrow Focuser", start: 24300, max: 60000);
        var narrowWheel = host.AddSimulatedFilterWheel(
            NarrowFilterWheelId, "Narrow Filter Wheel", NarrowFilters, moveDuration: options.FilterWheelMoveDuration);
        host.AddRig(new Rig(
            NarrowRigId, "Narrow Rig", narrow.Id,
            new OpticalTrain(1200, 200, 3.76, 3.76, 4656, 3520),
            narrowFocuser.Id, narrowWheel.Id));
        host.AddSimulatedFocusModel(NarrowRigId, new SimulatedFocusModel(NarrowBestFocus));
    }

    public static DemoEquipment AddDemoEquipment(SideraRuntimeHost host, DemoOptions? options = null)
    {
        options ??= new DemoOptions();

        var camera = host.AddSimulatedCamera(MainCameraId, "Main Camera");
        var focuser = AddFocuser(host, options, MainFocuserId, "Main Focuser", start: 18200, max: 50000);
        var wheel = host.AddSimulatedFilterWheel(
            MainFilterWheelId, "Main Filter Wheel", MainFilters, moveDuration: options.FilterWheelMoveDuration);
        var mount = host.AddSimulatedMount(MountId, "EQ6 Mount", options.SlewDuration);
        var guider = host.AddSimulatedGuider(
            GuiderId, "Main Guider",
            options.GuiderStartDuration, options.GuiderStopDuration, options.GuiderDitherDuration);

        var rig = new Rig(
            MainRigId,
            "Main Rig",
            camera.Id,
            new OpticalTrain(
                focalLengthMm: 750,
                apertureMm: 150,
                pixelSizeXMicrons: 3.76,
                pixelSizeYMicrons: 3.76,
                sensorWidthPixels: 6248,
                sensorHeightPixels: 4176),
            focuser.Id,
            wheel.Id);
        host.AddRig(rig);
        host.AddSimulatedFocusModel(MainRigId, new SimulatedFocusModel(MainBestFocus));

        return new DemoEquipment(camera, mount, guider, rig);
    }

    /// <summary>
    /// The demo as stored equipment: the same devices and the same three rigs that <see cref="AddDemoEquipment"/> and
    /// <see cref="AddDemoRigs"/> compose in code, as simulator devices and rigs of an equipment file. Adding it to an
    /// installation (see <c>EquipmentService.AddDemoEquipment</c>) is how the demo is started now that a first start has no
    /// equipment.
    /// </summary>
    public static EquipmentConfiguration Configuration()
    {
        static DeviceConfiguration Simulated(DeviceId id, string name, DeviceType type, params (string Key, string Value)[] settings) =>
            DeviceConfiguration.Simulator(id.Value, name, type, settings.ToDictionary(s => s.Key, s => s.Value));

        static string Names(IEnumerable<FilterSlot> slots) => string.Join(",", slots.Select(s => s.Name));
        static (string, string) Start(int position) => (SimulatorDeviceFactory.StartPositionKey, position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        static (string, string) Max(int position) => (SimulatorDeviceFactory.MaxPositionKey, position.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new EquipmentConfiguration(
            [
                Simulated(MainCameraId, "Main Camera", DeviceType.Camera),
                Simulated(MainFocuserId, "Main Focuser", DeviceType.Focuser, Start(18200), Max(50000)),
                Simulated(MainFilterWheelId, "Main Filter Wheel", DeviceType.FilterWheel, (SimulatorDeviceFactory.FiltersKey, Names(MainFilters))),
                Simulated(MountId, "EQ6 Mount", DeviceType.Mount),
                Simulated(GuiderId, "Main Guider", DeviceType.Guider),
                Simulated(WideCameraId, "Wide Camera", DeviceType.Camera),
                Simulated(WideFocuserId, "Wide Focuser", DeviceType.Focuser, Start(5200), Max(12000)),
                Simulated(NarrowCameraId, "Narrow Camera", DeviceType.Camera),
                Simulated(NarrowFocuserId, "Narrow Focuser", DeviceType.Focuser, Start(24300), Max(60000)),
                Simulated(NarrowFilterWheelId, "Narrow Filter Wheel", DeviceType.FilterWheel, (SimulatorDeviceFactory.FiltersKey, Names(NarrowFilters))),
            ],
            [
                new RigConfiguration(
                    MainRigId.Value, "Main Rig", MainCameraId.Value, MainFocuserId.Value, MainFilterWheelId.Value,
                    new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176), MainBestFocus),
                new RigConfiguration(
                    WideRigId.Value, "Wide Rig", WideCameraId.Value, WideFocuserId.Value, null,
                    new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176), WideBestFocus),
                new RigConfiguration(
                    NarrowRigId.Value, "Narrow Rig", NarrowCameraId.Value, NarrowFocuserId.Value, NarrowFilterWheelId.Value,
                    new OpticalTrain(1200, 200, 3.76, 3.76, 4656, 3520), NarrowBestFocus),
            ]);
    }

    private static SimulatedFocuser AddFocuser(
        SideraRuntimeHost host, DemoOptions options, DeviceId id, string name, int start, int max) =>
        host.AddSimulatedFocuser(
            id, name, startPosition: start, minPosition: 0, maxPosition: max,
            stepsPerSecond: options.FocuserStepsPerSecond, minimumMoveDuration: options.FocuserMinimumMoveDuration);
}
