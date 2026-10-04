using Astra.Desktop.ViewModels;

namespace Astra.Desktop.Tests.Ux;

/// <summary>The equipment page as a master-detail workspace: a browser of devices (or rigs) and the detail of the selected one.</summary>
public class EquipmentWorkspaceTests
{
    [Fact]
    public async Task TheDevicesModeIsTheDefault_RigsAreAnotherModeOfTheSamePage()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        Assert.Equal(EquipmentMode.Devices, equipment.Mode);
        Assert.True(equipment.IsDevicesMode);
        Assert.False(equipment.IsRigsMode);
        Assert.True(equipment.HasRigs);

        equipment.IsRigsMode = true;

        Assert.Equal(EquipmentMode.Rigs, equipment.Mode);
        Assert.False(equipment.IsDevicesMode);
    }

    [Fact]
    public async Task TheFirstDeviceIsSelectedFromTheStart_SoTheWorkspaceIsNeverEmpty()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        Assert.Equal("camera.main", equipment.SelectedDevice!.DeviceIdText);
        Assert.IsType<CameraDetailViewModel>(equipment.SelectedDetail);
        Assert.Same(equipment.SelectedDevice, equipment.SelectedDetail!.Device);
        Assert.Equal(["camera.main"], equipment.Devices.Where(d => d.IsSelected).Select(d => d.DeviceIdText));
        Assert.Equal("rig.main", equipment.SelectedRig!.RigIdText);
    }

    [Fact]
    public async Task SelectingADeviceOfEachKind_ShowsTheDetailOfThatKind()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        equipment.Focusers[0].OpenCommand!.Execute(null);
        Assert.IsType<FocuserDetailViewModel>(equipment.SelectedDetail);

        equipment.FilterWheels[0].OpenCommand!.Execute(null);
        Assert.IsType<FilterWheelDetailViewModel>(equipment.SelectedDetail);

        equipment.Mounts[0].OpenCommand!.Execute(null);
        Assert.IsType<MountDetailViewModel>(equipment.SelectedDetail);

        equipment.Guiders[0].OpenCommand!.Execute(null);
        Assert.IsType<GuiderDetailViewModel>(equipment.SelectedDetail);

        equipment.Cameras[1].OpenCommand!.Execute(null);
        var camera = Assert.IsType<CameraDetailViewModel>(equipment.SelectedDetail);
        Assert.Same(equipment.Cameras[1], camera.Camera);
        Assert.Equal(1, equipment.Devices.Count(d => d.IsSelected)); // one row is marked, always
    }

    [Fact]
    public async Task TheDetailOfADevice_IsTheSameWhenItIsSelectedAgain_AndKeepsItsTab()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        var wheel = equipment.FilterWheels[0];

        wheel.OpenCommand!.Execute(null);
        var first = equipment.SelectedDetail!;
        Assert.Equal(DeviceDetailSection.Overview, first.Section);
        first.IsControlsSection = true;

        equipment.Mounts[0].OpenCommand!.Execute(null);
        wheel.OpenCommand.Execute(null);

        Assert.Same(first, equipment.SelectedDetail);
        Assert.Equal(DeviceDetailSection.Controls, equipment.SelectedDetail!.Section);
    }

    [Fact]
    public async Task TheTabsOfADetail_AreExclusive()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var detail = app.Vm.Equipment.SelectedDetail!;

        Assert.True(detail.IsOverviewSection);

        detail.IsSettingsSection = true;

        Assert.Equal(DeviceDetailSection.Settings, detail.Section);
        Assert.Equal(
            [false, false, true, false], [detail.IsOverviewSection, detail.IsControlsSection, detail.IsSettingsSection, detail.IsDriverInfoSection]);

        detail.IsDriverInfoSection = true;
        detail.IsDriverInfoSection = false; // un-checking a radio button picks nothing

        Assert.Equal(DeviceDetailSection.DriverInfo, detail.Section);
    }

    [Fact]
    public async Task TheDetail_ReflectsTheConnection_AndConnectsAndDisconnectsTheDevice()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var detail = app.Vm.Equipment.SelectedDetail!;
        var camera = detail.Device;

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
        Assert.Equal("RA 5.5 h · Dec -5°", mount.Mount.CoordinatesText);

        var guider = (GuiderDetailViewModel)equipment.DetailOf(equipment.Guiders[0]);
        await guider.Guider.StartGuidingCommand.ExecuteAsync(null);
        Assert.Equal("Guiding", guider.Guider.ActivityText);
        await guider.Guider.StopGuidingCommand.ExecuteAsync(null);
        Assert.NotEqual("Guiding", guider.Guider.ActivityText);
    }

    [Fact]
    public async Task NoDevice_OffersSettingsItDoesNotHave_TheSettingsTabSaysWhichOnesWillComeInstead()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);

        var details = app.Vm.Equipment.Devices.Select(app.Vm.Equipment.DetailOf).ToList();

        Assert.Equal(10, details.Count);
        Assert.All(details, d => Assert.False(string.IsNullOrWhiteSpace(d.NoSettingsText)));
        Assert.Equal(5, details.Select(d => d.NoSettingsText).Distinct().Count()); // one text per kind of device, not one generic text
    }

    // Without a rig, with one

    [Fact]
    public async Task WithoutRigs_TheDevicesAreComplete_AndNoDetailNeedsARig()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var equipment = app.Vm.Equipment;

        Assert.False(equipment.HasRigs);
        Assert.Null(equipment.SelectedRig);
        equipment.IsRigsMode = true;
        Assert.Equal(EquipmentMode.Devices, equipment.Mode);

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
    public async Task SelectingARig_ShowsItsDetail_AndLeavesTheSelectedDeviceAlone()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        var device = equipment.SelectedDevice;
        var wide = equipment.Rigs.Single(r => r.RigIdText == "rig.wide");

        wide.OpenCommand!.Execute(null);

        Assert.Equal(EquipmentMode.Rigs, equipment.Mode);
        Assert.Same(wide, equipment.SelectedRig);
        Assert.Equal(["rig.wide"], equipment.Rigs.Where(r => r.IsSelected).Select(r => r.RigIdText));
        Assert.Same(device, equipment.SelectedDevice);
        Assert.Equal([true, true, false], wide.Members.Select(m => m.IsConfigured)); // no filter wheel: said, not hidden
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
        Assert.Equal(equipment.Devices.Count(), equipment.Sections.Sum(s => s.Items.Count)); // the rig adds no device of its own
    }
}
