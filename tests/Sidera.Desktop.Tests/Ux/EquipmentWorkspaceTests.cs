using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The equipment workspace: contexts (the standalone devices and each rig), the pages of a context, the device of a page, a way
/// back, and an overview to start from. Rigs are optional and never needed.
/// </summary>
public class EquipmentWorkspaceTests
{
    private static EquipmentContextViewModel Context(EquipmentViewModel equipment, string title) => equipment.Contexts.Single(c => c.Title == title);

    private static void OpenPage(EquipmentViewModel equipment, string title) => equipment.Pages.Single(p => p.Title == title).SelectCommand.Execute(null);

    // ---- The overview

    [Fact]
    public async Task TheWorkspaceStartsWithAnOverviewOfAllTheEquipment()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        Assert.True(equipment.IsLanding);
        Assert.Null(equipment.SelectedContext);
        Assert.False(equipment.IsDeviceWorkspace);
        Assert.Null(equipment.SelectedDevice);
        Assert.Equal(["Main Rig", "Narrow Rig", "Wide Rig", "Standalone devices"], equipment.LandingGroups.Select(g => g.Title));
        Assert.Equal(["Camera", "Focuser", "Filter Wheel"], equipment.LandingGroups[0].Rows.Select(r => r.Role));
        Assert.Equal(["Camera", "Focuser"], equipment.LandingGroups[2].Rows.Select(r => r.Role)); // the wide rig has no filter wheel
        Assert.Equal(["Mount", "Guider"], equipment.LandingGroups[3].Rows.Select(r => r.Role)); // shared equipment is not the rigs'
        Assert.Equal("3 devices · 0 connected", equipment.LandingGroups[0].SummaryText);
        Assert.False(equipment.CanGoBack);
        Assert.Equal(["Equipment"], equipment.Breadcrumb.Select(b => b.Title));
    }

    [Fact]
    public async Task TheOverviewFollowsTheConnections_AndItsRowsOpenThePageOfTheirDevice()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        var main = equipment.LandingGroups[0];

        await equipment.Cameras.Single(c => c.DeviceIdText == "camera.main").ConnectCommand.ExecuteAsync(null);
        Assert.Equal("3 devices · 1 connected", main.SummaryText);

        main.Rows.Single(r => r.Role == "Focuser").OpenCommand.Execute(null);

        Assert.Equal("Main Rig", equipment.SelectedContext!.Title);
        Assert.Equal(EquipmentPage.Focuser, equipment.SelectedPage);
        Assert.Equal("focuser.main", equipment.SelectedDevice!.DeviceIdText);

        equipment.ShowOverviewCommand.Execute(null);
        equipment.LandingGroups[3].Rows.Single(r => r.Role == "Mount").OpenCommand.Execute(null);

        Assert.Equal("Standalone", equipment.SelectedContext!.Title);
        Assert.Equal(EquipmentPage.Mount, equipment.SelectedPage);
        Assert.IsType<MountDetailViewModel>(equipment.SelectedDetail);
    }

    // ---- Standalone: a page for each kind of device, and a choice of the device

    [Fact]
    public async Task StandaloneHasAPageForEachKindOfDevice_AndOnlyForKindsThatThereAre()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        Context(equipment, "Standalone").SelectCommand.Execute(null);

        Assert.Equal(["Camera", "Mount", "Focuser", "Filter Wheel", "Guider"], equipment.Pages.Select(p => p.Title));
        Assert.Equal(EquipmentPage.Camera, equipment.SelectedPage);
        Assert.True(equipment.IsDeviceWorkspace);
        Assert.Equal(["Camera"], equipment.Breadcrumb.Select(b => b.Title).Skip(1));
    }

    [Fact]
    public async Task SeveralDevicesOfAKind_AreChosenFromAList_NotShownAsCards()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Standalone").SelectCommand.Execute(null);

        Assert.True(equipment.HasDeviceChooser);
        Assert.Equal(["camera.main", "camera.narrow", "camera.wide"], equipment.DeviceChoices.Select(d => d.DeviceIdText));
        Assert.Equal("camera.main", equipment.SelectedDevice!.DeviceIdText);
        Assert.Equal(1, equipment.Devices.Count(d => d.IsSelected));

        equipment.ChosenDevice = equipment.DeviceChoices[2];

        Assert.Equal("camera.wide", equipment.SelectedDevice!.DeviceIdText);
        var camera = Assert.IsType<CameraDetailViewModel>(equipment.SelectedDetail);
        Assert.Equal("camera.wide", camera.Device.DeviceIdText);
        Assert.Equal(1, equipment.Devices.Count(d => d.IsSelected));
    }

    [Fact]
    public async Task AKindWithOneDevice_HasNoChooser()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Standalone").SelectCommand.Execute(null);

        OpenPage(equipment, "Mount");

        Assert.False(equipment.HasDeviceChooser);
        Assert.Equal("mount.eq6", equipment.SelectedDevice!.DeviceIdText);
    }

    [Fact]
    public async Task TheChosenDeviceOfEachKind_IsRemembered_AsIsWhereTheUserWas()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Standalone").SelectCommand.Execute(null);
        equipment.ChosenDevice = equipment.DeviceChoices[1];
        OpenPage(equipment, "Focuser");
        equipment.ChosenDevice = equipment.DeviceChoices[2];

        // Away to a rig and back to the standalone devices, and to the overview and back.
        Context(equipment, "Main Rig").SelectCommand.Execute(null);
        equipment.ShowOverviewCommand.Execute(null);
        Context(equipment, "Standalone").SelectCommand.Execute(null);

        Assert.Equal(EquipmentPage.Focuser, equipment.SelectedPage);
        Assert.Equal("focuser.wide", equipment.SelectedDevice!.DeviceIdText);
        OpenPage(equipment, "Camera");
        Assert.Equal("camera.narrow", equipment.SelectedDevice!.DeviceIdText);
    }

    [Fact]
    public async Task OpeningADeviceByItsCommand_GoesToItsStandalonePage()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        equipment.Focusers.Single(f => f.DeviceIdText == "focuser.wide").OpenCommand!.Execute(null);

        Assert.Equal("Standalone", equipment.SelectedContext!.Title);
        Assert.Equal("focuser.wide", equipment.SelectedDevice!.DeviceIdText);
        Assert.IsType<FocuserDetailViewModel>(equipment.SelectedDetail);
        Assert.Equal([equipment.Focusers.Single(f => f.DeviceIdText == "focuser.wide")], equipment.Devices.Where(d => d.IsSelected));
    }

    // ---- Rigs

    [Fact]
    public async Task ARigHasAnOverviewAndThePagesOfItsDevices_AndNeverTheSharedMountOrGuider()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        Context(equipment, "Main Rig").SelectCommand.Execute(null);

        Assert.True(equipment.IsRigOverview);
        Assert.Equal(["Overview", "Camera", "Focuser", "Filter Wheel"], equipment.Pages.Select(p => p.Title));
        Assert.Equal("rig.main", equipment.SelectedRig!.RigIdText);
        Assert.Equal(["Equipment", "Main Rig"], equipment.Breadcrumb.Select(b => b.Title));
        Assert.Equal("Equipment", equipment.BackText);
    }

    [Fact]
    public async Task ARigPage_ControlsTheDeviceOfTheRig_WithoutAChooser_AndSaysWhereItIs()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Wide Rig").SelectCommand.Execute(null);

        OpenPage(equipment, "Focuser");

        Assert.False(equipment.HasDeviceChooser);
        Assert.Equal("focuser.wide", equipment.SelectedDevice!.DeviceIdText);
        Assert.Equal(["Equipment", "Wide Rig", "Focuser"], equipment.Breadcrumb.Select(b => b.Title));
        Assert.True(equipment.Breadcrumb[1].IsLink);
        Assert.True(equipment.Breadcrumb[2].IsCurrent);
        Assert.Equal("Wide Rig", equipment.BackText);
        Assert.False(equipment.HasMissingDevice);
    }

    [Fact]
    public async Task ARigWithoutAFilterWheel_HasNoFilterWheelPage_AndFallsBackToItsOverviewNeverToAnotherWheel()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Main Rig").SelectCommand.Execute(null);
        OpenPage(equipment, "Filter Wheel");
        Assert.Equal("filterwheel.main", equipment.SelectedDevice!.DeviceIdText);

        Context(equipment, "Wide Rig").SelectCommand.Execute(null);

        Assert.DoesNotContain(equipment.Pages, p => p.Title == "Filter Wheel");
        Assert.True(equipment.IsRigOverview);
        Assert.Null(equipment.SelectedDevice);
    }

    [Fact]
    public async Task BackGoesOnePlaceUp_FromADevicePageToTheRig_FromTheRigToTheOverview()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Narrow Rig").SelectCommand.Execute(null);
        OpenPage(equipment, "Camera");

        equipment.BackCommand.Execute(null);
        Assert.True(equipment.IsRigOverview);
        Assert.Equal("Narrow Rig", equipment.SelectedContext!.Title);

        equipment.BackCommand.Execute(null);
        Assert.True(equipment.IsLanding);
        Assert.False(equipment.CanGoBack);
    }

    [Fact]
    public async Task TheBreadcrumbLinks_GoToThePlacesAbove()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Main Rig").SelectCommand.Execute(null);
        OpenPage(equipment, "Camera");

        equipment.Breadcrumb[1].Command!.Execute(null);
        Assert.True(equipment.IsRigOverview);

        equipment.Breadcrumb[0].Command!.Execute(null);
        Assert.True(equipment.IsLanding);
    }

    [Fact]
    public async Task ThePageOfARig_IsRememberedPerContext()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Main Rig").SelectCommand.Execute(null);
        OpenPage(equipment, "Focuser");
        Context(equipment, "Wide Rig").SelectCommand.Execute(null);
        OpenPage(equipment, "Camera");

        Context(equipment, "Main Rig").SelectCommand.Execute(null);

        Assert.Equal(EquipmentPage.Focuser, equipment.SelectedPage);
        Assert.Equal("focuser.main", equipment.SelectedDevice!.DeviceIdText);
    }

    // ---- The device page

    [Fact]
    public async Task TheDetail_ReflectsTheConnection_AndConnectsAndDisconnectsTheDevice()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var equipment = app.Vm.Equipment;
        Context(equipment, "Standalone").SelectCommand.Execute(null);
        var camera = equipment.SelectedDetail!.Device;

        Assert.Equal("Disconnected", camera.StatusText);
        Assert.Equal(StatusKind.Neutral, camera.StatusKind);
        Assert.True(camera.ConnectCommand.CanExecute(null));

        await camera.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Connected", camera.StatusText);
        Assert.Equal(StatusKind.Ok, camera.StatusKind);
        Assert.Equal("Idle", camera.ActivityText);
        Assert.True(camera.DisconnectCommand.CanExecute(null));

        await camera.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal("Disconnected", camera.StatusText);
    }

    [Fact]
    public async Task EveryKindOfDetail_CarriesTheControlsOfItsDevice_AsTheyWorkToday()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig, connect: true);
        var equipment = app.Vm.Equipment;

        var camera = (CameraDetailViewModel)equipment.DetailOf(equipment.Cameras[0]);
        await camera.Camera.StartExposureCommand.ExecuteAsync(null);
        Assert.Contains("exposure", camera.Camera.LastFrameText);
        Assert.NotNull(app.Vm.Imaging.LatestFrame);

        var focuser = (FocuserDetailViewModel)equipment.DetailOf(equipment.Focusers[0]);
        focuser.Focuser.TargetInput = "30000";
        await focuser.Focuser.MoveCommand.ExecuteAsync(null);
        Assert.Equal("30000 steps", focuser.Focuser.PositionText);

        var wheel = (FilterWheelDetailViewModel)equipment.DetailOf(equipment.FilterWheels[0]);
        wheel.Wheel.SelectedFilter = wheel.Wheel.Filters[3];
        await wheel.Wheel.ChangeCommand.ExecuteAsync(null);
        Assert.Equal(wheel.Wheel.Filters[3].Name, wheel.Wheel.CurrentFilterText);

        var mount = (MountDetailViewModel)equipment.DetailOf(equipment.Mounts[0]);
        mount.Mount.RightAscensionInput = "5.5";
        mount.Mount.DeclinationInput = "-5";
        await mount.Mount.SlewCommand.ExecuteAsync(null);
        Assert.True(mount.Mount.IsConfirmingLargeSlew); // 80 degrees away from where the simulator points
        await mount.Mount.ConfirmSlewCommand.ExecuteAsync(null);
        Assert.Equal("RA 5.5 h · Dec -5°", mount.Mount.CoordinatesText);

        var guider = (GuiderDetailViewModel)equipment.DetailOf(equipment.Guiders[0]);
        await guider.Guider.StartGuidingCommand.ExecuteAsync(null);
        Assert.Equal("Guiding", guider.Guider.ActivityText);
        await guider.Guider.StopGuidingCommand.ExecuteAsync(null);
        Assert.NotEqual("Guiding", guider.Guider.ActivityText);
    }

    [Fact]
    public async Task AMountPage_HasAStopThatIsAlwaysThere_AndTheOthersHaveNone()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig, connect: true);
        var equipment = app.Vm.Equipment;

        Assert.True(equipment.DetailOf(equipment.Mounts[0]).HasStop);
        Assert.False(equipment.DetailOf(equipment.Cameras[0]).HasStop);
        Assert.False(equipment.DetailOf(equipment.Focusers[0]).HasStop);
        Assert.False(equipment.DetailOf(equipment.Guiders[0]).HasStop);
    }

    [Fact]
    public async Task NoDevice_NeedsATabOrASettingsPage_TheDetailIsAWorkspace()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        var details = equipment.Devices.Select(equipment.DetailOf).ToList();

        Assert.Equal(10, details.Count);
        Assert.All(details, d => Assert.NotNull(d.Device));
    }

    // ---- Without a rig, with one

    [Fact]
    public async Task WithoutRigs_TheDevicesAreComplete_AndNoDetailNeedsARig()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var equipment = app.Vm.Equipment;

        Assert.False(equipment.HasRigs);
        Assert.Null(equipment.SelectedRig);
        Context(equipment, "Standalone").SelectCommand.Execute(null);

        var camera = Assert.IsType<CameraDetailViewModel>(equipment.SelectedDetail);
        Assert.False(camera.HasRig);
        Assert.Equal("Not part of a rig", camera.RigText);
        Assert.Equal("Not reported", camera.ResolutionText);

        await camera.Camera.StartExposureCommand.ExecuteAsync(null);

        Assert.NotNull(app.Vm.Imaging.LatestFrame);
        Assert.False(camera.Camera.HasError);
    }

    [Fact]
    public async Task ACameraInARig_ShowsTheSensorTheRigDescribes_AndSaysWhereItCameFrom()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        var rig = equipment.Rigs.Single(r => r.RigIdText == "rig.main");

        var camera = (CameraDetailViewModel)equipment.DetailOf(equipment.Cameras.Single(c => c.DeviceIdText == "camera.main"));

        Assert.Same(rig, camera.Rig);
        Assert.Equal("Main Rig", camera.RigText);
        Assert.Equal($"{rig.ResolutionText} (from Main Rig)", camera.ResolutionText);
        Assert.Equal($"{rig.PixelSizeText} (from Main Rig)", camera.PixelSizeText);
    }

    [Fact]
    public async Task TheStateOfARig_IsReadFromItsDevices_WhileTheDeviceListShowsTheSameDevices()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var equipment = app.Vm.Equipment;
        var rig = equipment.Rigs.Single();

        await equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Partly connected", rig.StatusText);
        Assert.Equal("Idle", rig.ActivityText);
        Assert.Equal(StatusKind.Ok, equipment.Cameras[0].StatusKind);
        Assert.Equal(equipment.Devices.Count(), equipment.LandingGroups.Sum(g => g.Rows.Count)); // the rig adds no device of its own
    }
}
