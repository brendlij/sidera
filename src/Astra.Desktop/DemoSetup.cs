using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Coordination;
using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Rigs;
using Astra.Runtime;
using Astra.Runtime.Devices;
using Astra.Runtime.Focusing;

namespace Astra.Desktop;

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
    public static void AddDemoRigs(AstraRuntimeHost host, DemoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new DemoOptions();

        var wide = host.AddSimulatedCamera(WideCameraId, "Wide Camera");
        var wideFocuser = AddFocuser(host, options, WideFocuserId, "Wide Focuser", start: 5200, max: 12000);
        host.AddRig(new Rig(
            WideRigId, "Wide Rig", wide.Id,
            new OpticalTrain(250, 60, 3.76, 23.5, 15.7, 6248, 4176),
            wideFocuser.Id));
        host.AddSimulatedFocusModel(WideRigId, new SimulatedFocusModel(WideBestFocus));

        var narrow = host.AddSimulatedCamera(NarrowCameraId, "Narrow Camera");
        var narrowFocuser = AddFocuser(host, options, NarrowFocuserId, "Narrow Focuser", start: 24300, max: 60000);
        var narrowWheel = host.AddSimulatedFilterWheel(
            NarrowFilterWheelId, "Narrow Filter Wheel", NarrowFilters, moveDuration: options.FilterWheelMoveDuration);
        host.AddRig(new Rig(
            NarrowRigId, "Narrow Rig", narrow.Id,
            new OpticalTrain(1200, 200, 3.76, 17.6, 13.2, 4656, 3520),
            narrowFocuser.Id, narrowWheel.Id));
        host.AddSimulatedFocusModel(NarrowRigId, new SimulatedFocusModel(NarrowBestFocus));
    }

    public static DemoEquipment AddDemoEquipment(AstraRuntimeHost host, DemoOptions? options = null)
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
                pixelSizeMicrons: 3.76,
                sensorWidthMm: 23.5,
                sensorHeightMm: 15.7,
                resolutionWidth: 6248,
                resolutionHeight: 4176),
            focuser.Id,
            wheel.Id);
        host.AddRig(rig);
        host.AddSimulatedFocusModel(MainRigId, new SimulatedFocusModel(MainBestFocus));

        return new DemoEquipment(camera, mount, guider, rig);
    }

    private static SimulatedFocuser AddFocuser(
        AstraRuntimeHost host, DemoOptions options, DeviceId id, string name, int start, int max) =>
        host.AddSimulatedFocuser(
            id, name, startPosition: start, minPosition: 0, maxPosition: max,
            stepsPerSecond: options.FocuserStepsPerSecond, minimumMoveDuration: options.FocuserMinimumMoveDuration);
}
