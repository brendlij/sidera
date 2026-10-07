using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The imaging setup of the application: one setup is just there, several are chosen in the sidebar, and Imaging, Autofocus, Framing, Plate Solve, the equipment view of a setup, the dashboard and
/// new targets all follow the choice, none of them asking again which setup or which camera is meant.
/// </summary>
public sealed class SetupContextTests
{
    private static async Task<(SideraRuntimeHost Host, MainViewModel Vm)> BareAsync(params string[] cameras)
    {
        var host = new SideraRuntimeHost();
        foreach (var name in cameras)
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
        }

        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        return (host, new MainViewModel(host, a => a(), new DemoOptions()));
    }

    private static void OpenEquipmentFromTheSidebar(MainViewModel vm) =>
        vm.PrimaryNavigation.Single(i => i.Page == AppPage.Equipment).Command.Execute(null);

    // ---- one setup, several setups, none

    [Fact]
    public async Task OneCamera_IsTheImagingSetup_WithNothingToChoose()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var context = app.Vm.SetupContext;

        Assert.True(context.HasCurrent);
        Assert.True(context.HasOne);
        Assert.False(context.HasSeveral);
        Assert.Equal("Main Camera", context.Name);
        Assert.Equal(ImagingSetupCatalog.ImplicitIdFor(DemoSetup.MainCameraId), context.Current!.Id);
        Assert.Equal(string.Empty, context.NoSetupText);
    }

    [Fact]
    public async Task SeveralSetups_OfferTheChoice_AndEveryPageFollowsIt()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var vm = app.Vm;
        var context = vm.SetupContext;

        Assert.True(context.HasSeveral);
        Assert.Equal(["Main Rig", "Narrow Rig", "Wide Rig"], context.Options.Select(o => o.Name));
        Assert.Equal("Main Rig", context.Name);
        Assert.Equal(new RigId("rig.main"), vm.Framing.Setup!.Id);
        Assert.Equal(new RigId("rig.main"), vm.PlateSolve.Setup!.Id);
        Assert.Equal(new RigId("rig.main"), vm.Imaging.Capture!.SelectedTarget!.RigId);
        Assert.Equal(new RigId("rig.main"), vm.Imaging.Autofocus!.Setup!.Id);
        Assert.Equal("Main Rig", vm.Equipment.Summary!.Name);

        context.Choose("Wide Rig");

        Assert.Equal(new RigId("rig.wide"), vm.Framing.Setup!.Id);
        Assert.Equal(new RigId("rig.wide"), vm.PlateSolve.Setup!.Id);
        Assert.Equal(new RigId("rig.wide"), vm.Imaging.Capture.SelectedTarget!.RigId);
        Assert.Equal(new RigId("rig.wide"), vm.Imaging.Autofocus.Setup!.Id);
        Assert.Equal("Wide Rig", vm.Equipment.Summary.Name);
        Assert.Equal(DemoSetup.MountId, vm.Framing.SelectedMount!.Id); // the setups share the mount: switching the setup does not move or change it
    }

    [Fact]
    public async Task TheChoice_StaysWithTheCamera_WhenAnExplicitSetupIsMadeForIt()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var context = app.Vm.SetupContext;
        var before = context.Selected!.Id;

        app.Host.AddRig(new Rig(new("rig.main"), "Main 750", DemoSetup.MainCameraId, new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176)));
        context.Refresh();

        Assert.Equal(before, context.Selected!.Id); // the same camera, the same path
        Assert.Equal("Main 750", context.Name);
        Assert.NotNull(context.Current!.Optics);
        Assert.Equal(750, app.Vm.Framing.Setup!.Optics!.FocalLengthMm); // the pages have the optics as soon as the setup exists
    }

    [Fact]
    public async Task SeveralCamerasAndNoSetup_HaveNoCurrentSetup_AndEveryPageSaysWhatToDo()
    {
        var (host, vm) = await BareAsync("a", "b");
        await using var _ = host;
        using var __ = vm;
        var context = vm.SetupContext;

        Assert.False(context.HasCurrent);
        Assert.True(context.NeedsSetup);
        Assert.Equal("Multiple imaging paths are available. Create or choose an Imaging Setup.", context.NoSetupText);
        Assert.Null(vm.Framing.Setup);
        Assert.False(vm.Framing.HasSetup);
        Assert.False(vm.PlateSolve.HasSetup);
        Assert.Contains("Multiple imaging paths", vm.PlateSolve.HintText, StringComparison.Ordinal);
        Assert.True(vm.Imaging.Capture!.NeedsSetup);
        Assert.Null(vm.Equipment.Summary);
        Assert.False(vm.Equipment.IsSetupView);

        vm.Imaging.Capture.CreateSetupHereCommand.Execute(null);

        Assert.Equal(AppPage.Equipment, vm.SelectedPage); // where the setup is made, with the camera chosen there
        Assert.False(vm.Equipment.IsSetupView);
    }

    [Fact]
    public async Task SeveralCamerasAndNoSetup_TheSessionSaysSo_AndOffersToCreateOne()
    {
        var (host, vm) = await BareAsync("a", "b");
        await using var _ = host;
        using var __ = vm;
        var editor = vm.SessionEditor;

        editor.Load(SessionDefinition.Empty);
        editor.AddTargetCommand.Execute(null);

        Assert.True(editor.HasSetupNotice);
        Assert.Equal("Multiple imaging paths are available. Create or choose an Imaging Setup.", editor.SetupNotice);
        Assert.Contains(editor.Problems, p => p.Contains("No imaging setup", StringComparison.Ordinal)); // and the sequence is not run with a camera that was guessed

        editor.CreateImagingSetupCommand.Execute(null);

        Assert.Equal(AppPage.Equipment, vm.SelectedPage);
    }

    // ---- the equipment of the current setup

    [Fact]
    public async Task TheEquipmentPage_StartsWithTheCurrentSetup_AndManageAllDevicesOpensEverything()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var vm = app.Vm;

        OpenEquipmentFromTheSidebar(vm);

        Assert.Equal(AppPage.Equipment, vm.SelectedPage);
        Assert.True(vm.Equipment.IsSetupView);
        Assert.False(vm.Equipment.ShowSectionTabs); // no tabs of kinds of devices here
        var summary = vm.Equipment.Summary!;
        Assert.Equal("MAIN RIG", summary.Title);
        Assert.Equal(["Camera", "Focuser", "Filter Wheel"], summary.Own.Select(p => p.Role));
        Assert.Equal(["Mount", "Guider"], summary.Shared.Select(p => p.Role));
        Assert.True(summary.HasOptics);

        vm.Equipment.ManageAllDevicesCommand.Execute(null);

        Assert.False(vm.Equipment.IsSetupView);
        Assert.True(vm.Equipment.CanReturnToSetup);
        Assert.Equal("‹ Main Rig", vm.Equipment.ReturnToSetupText);
        Assert.True(vm.Equipment.IsLanding);
        Assert.True(vm.Equipment.ShowSectionTabs);

        vm.Equipment.ShowSetupViewCommand.Execute(null);

        Assert.True(vm.Equipment.IsSetupView);
    }

    [Fact]
    public async Task ADeviceOfTheSetup_OpensItsPage_InTheManagementOfTheDevices()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var vm = app.Vm;
        OpenEquipmentFromTheSidebar(vm);

        vm.Equipment.Summary!.Own.Single(p => p.Role == "Focuser").OpenCommand!.Execute(null);

        Assert.False(vm.Equipment.IsSetupView);
        Assert.NotNull(vm.Equipment.SelectedDevice);
        Assert.IsType<FocuserViewModel>(vm.Equipment.SelectedDevice);
    }

    [Fact]
    public async Task ASetupWithoutAFocuser_DoesNotShowOne_AndTheSummaryOfAnotherSetupFollowsTheSwitch()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SetupContext.Choose("Wide Rig");

        Assert.DoesNotContain(app.Vm.Equipment.Summary!.Own, p => p.Role == "Filter Wheel"); // the wide setup has none
    }

    [Fact]
    public async Task TheSetupOfOneCamera_HasASummary_WithoutOptics_AndSaysHowToGetThem()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);

        var summary = app.Vm.Equipment.Summary!;

        Assert.True(summary.IsImplicit);
        Assert.False(summary.HasOptics);
        Assert.True(summary.HasOpticsHint);
        Assert.Contains("Create an Imaging Setup", summary.OpticsHint, StringComparison.Ordinal);
        Assert.Equal(["Camera"], summary.Own.Select(p => p.Role));
        Assert.Equal(["Mount", "Guider"], summary.Shared.Select(p => p.Role));
    }

    // ---- the imaging page

    [Fact]
    public async Task WithoutAFocuser_TheAutofocusSaysSo_AndOffersToConfigureOne()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var autofocus = app.Vm.Imaging.Autofocus!;

        Assert.True(autofocus.HasSetup);
        Assert.False(autofocus.HasFocuser);
        Assert.True(autofocus.NeedsFocuser);

        autofocus.ConfigureFocuserHereCommand.Execute(null);

        Assert.Equal(AppPage.Equipment, app.Vm.SelectedPage);
        Assert.False(app.Vm.Equipment.IsSetupView);
        Assert.Equal(EquipmentSection.Focusers, app.Vm.Equipment.SelectedSection);
    }

    [Fact]
    public async Task TheCaptureOfOneCamera_NamesTheCamera_AndHasNothingToChoose()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var capture = app.Vm.Imaging.Capture!;

        Assert.Equal("Main Camera", capture.CameraName);
        Assert.Equal("Main Camera", capture.SourceName); // the setup and the camera have one name: it is said once
        Assert.False(capture.NeedsSetup);
    }

    // ---- the dashboard and the session

    [Fact]
    public async Task TheDashboard_CallsTheSetupsByTheirNumber_AndNeverSaysRig()
    {
        await using var several = await UxApp.Create(UxSetup.Demo, connect: true);
        await using var one = await UxApp.Create(UxSetup.OneRig, connect: true);
        await using var none = await UxApp.Create(UxSetup.Simple, connect: true);

        Assert.Equal("Imaging Setups", several.Vm.Dashboard.UnitsTitle);
        Assert.Equal("Shared", several.Vm.Dashboard.SharedTitle);
        Assert.Equal("Imaging Setup", one.Vm.Dashboard.UnitsTitle);
        Assert.Equal("Mount and guider", one.Vm.Dashboard.SharedTitle);
        Assert.Equal("Cameras", none.Vm.Dashboard.UnitsTitle);
        Assert.Equal("Imaging Setup", one.Vm.Dashboard.LanesTitle);
    }

    [Fact]
    public async Task ANewTarget_StartsWithTheSequenceOfTheCurrentSetup()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var editor = app.Vm.SessionEditor;
        editor.Load(SessionDefinition.Empty);
        app.Vm.SetupContext.Choose("Wide Rig");

        editor.AddTargetCommand.Execute(null);

        var lane = editor.Session!.Targets[0].Lanes.Single();
        Assert.Equal(ImagingBindingId.Of(app.Vm.SetupContext.Current!), lane.Setup);
        Assert.Equal("Wide Rig", editor.Targets[0].Lanes[0].SetupName);
    }

    [Fact]
    public async Task SelectingALaneOfTheSession_DoesNotChangeTheCurrentSetup_AndTheOtherWayRound()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var editor = app.Vm.SessionEditor;
        editor.Load(SessionDefinition.Empty);
        editor.AddTargetCommand.Execute(null);
        editor.Targets[0].AddLaneCommand.Execute(null);
        var target = editor.Session!.Targets[0];

        editor.SelectLane(target.Id, target.Lanes[1].Id);
        Assert.Equal("Main Rig", app.Vm.SetupContext.Name); // the lane tab edits a lane; the setup of the application is another thing

        editor.SelectLane(target.Id, target.Lanes[0].Id);
        app.Vm.SetupContext.Choose("Narrow Rig");
        Assert.Equal(target.Lanes[0].Id, editor.Targets[0].SelectedLane!.Id); // and the switcher does not move the lane that is shown
    }
}
