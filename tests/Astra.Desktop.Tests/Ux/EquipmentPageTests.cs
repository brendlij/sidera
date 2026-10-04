using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Desktop.ViewModels;

namespace Astra.Desktop.Tests.Ux;

/// <summary>The equipment workspace: devices are first-class, rigs are a group, every device has a detail of its own.</summary>
public class EquipmentPageTests
{
    [Fact]
    public async Task EveryDevice_IsReachableOnItsOwn_WhetherOrNotARigNamesIt()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        var ids = equipment.Devices.Select(d => d.DeviceIdText).ToList();

        Assert.Equal(
            ["camera.main", "camera.narrow", "camera.wide", "filterwheel.main", "filterwheel.narrow", "focuser.main", "focuser.narrow",
                "focuser.wide", "guider.main", "mount.eq6"],
            ids.Order());
        Assert.All(equipment.Devices, d => Assert.NotNull(d.OpenCommand));
        Assert.All(equipment.Rigs, r => Assert.NotNull(r.OpenCommand));
    }

    [Fact]
    public async Task SelectingADevice_ShowsItsDetail_ReplacingTheOneBefore()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        var focuser = equipment.Focusers.Single(f => f.DeviceIdText == "focuser.wide");

        focuser.OpenCommand!.Execute(null);

        Assert.Same(focuser, equipment.SelectedDevice);
        Assert.Same(focuser, Assert.IsType<FocuserDetailViewModel>(equipment.SelectedDetail).Focuser);
        Assert.Equal([focuser], equipment.Devices.Where(d => d.IsSelected)); // the row of the browser: one, and this one
    }

    [Fact]
    public async Task OpeningADeviceFromARig_StaysInTheRig_AndBackLeadsToTheRig()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        var rig = equipment.Rigs.Single(r => r.RigIdText == "rig.main");
        rig.OpenCommand!.Execute(null);
        Assert.True(equipment.IsRigOverview);

        rig.Members.Single(m => m.Role == "Camera").OpenCommand!.Execute(null);

        Assert.True(equipment.IsDeviceWorkspace);
        Assert.Equal("camera.main", equipment.SelectedDevice!.DeviceIdText);
        Assert.Same(rig, equipment.SelectedRig);
        Assert.False(equipment.HasDeviceChooser); // the rig decides the camera: nothing to choose

        equipment.BackCommand.Execute(null);

        Assert.True(equipment.IsRigOverview);
        Assert.Same(rig, equipment.SelectedRig);
    }

    [Fact]
    public async Task ARigWithoutAFilterWheel_SaysSoCleanly_AndTheOthersAreThere()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var wide = app.Vm.Equipment.Rigs.Single(r => r.RigIdText == "rig.wide");

        Assert.Equal(["Camera", "Focuser", "Filter Wheel"], wide.Members.Select(m => m.Role));
        Assert.Equal([true, true, false], wide.Members.Select(m => m.IsConfigured));
        Assert.Equal("Not configured", wide.Members.Single(m => m.Role == "Filter Wheel").NameText);
        Assert.Null(wide.FilterWheel);
        Assert.False(wide.HasFilterWheel);
        Assert.Equal("Wide Camera", wide.Members[0].NameText);
    }

    [Fact]
    public async Task ARigIsReadOnly_ItDescribesItsOpticsAndItsState_AndFollowsItsDevices()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var rig = app.Vm.Equipment.Rigs.Single();

        Assert.Equal("750 mm · f/5", rig.OpticsShortText);
        Assert.Equal("Disconnected", rig.StatusText);
        Assert.Equal(StatusKind.Neutral, rig.StatusKind);
        Assert.Equal("Not connected", rig.ActivityText);

        await app.Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Partly connected", rig.StatusText);
        Assert.Equal(StatusKind.Warning, rig.StatusKind);
        Assert.Equal("Idle", rig.ActivityText);

        await app.Vm.Equipment.Focusers[0].ConnectCommand.ExecuteAsync(null);
        await app.Vm.Equipment.FilterWheels[0].ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Connected", rig.StatusText);
        Assert.Equal(StatusKind.Ok, rig.StatusKind);
        Assert.Equal("18200 steps", rig.FocusText);
        Assert.Equal("L", rig.FilterText);
    }

    [Fact]
    public async Task ADeviceCard_SaysItsConnection_AndWhatItIsDoing()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var focuser = app.Vm.Equipment.Focusers[0];

        Assert.Equal("Disconnected", focuser.StatusText);
        Assert.Equal(StatusKind.Neutral, focuser.StatusKind);
        Assert.Equal(string.Empty, focuser.ActivityText);
        Assert.Equal("Focuser", focuser.KindTitle);
        Assert.Equal("Simulator", focuser.BackendText);

        await focuser.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Connected", focuser.StatusText);
        Assert.Equal(StatusKind.Ok, focuser.StatusKind);
        Assert.Equal("Idle · 18200 steps", focuser.ActivityText);
        Assert.Equal("Idle", focuser.MotionText);
        Assert.False(focuser.IsBusy);
    }

    [Fact]
    public async Task AMovingFocuser_IsBusy_WithItsMotionInTheCard()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig, connect: true);
        var focuser = app.Vm.Equipment.Focusers[0];
        focuser.TargetInput = "40000";

        var move = focuser.MoveCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => focuser.IsBusy, "the focuser moving");

        Assert.StartsWith("Moving", focuser.ActivityText);
        Assert.Equal("Moving", focuser.MotionText);
        await move;
        Assert.Equal("Idle · 40000 steps", focuser.ActivityText);
    }

    [Fact]
    public async Task ACameraCard_ShowsTheExposureAsProgress()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var camera = app.Vm.Equipment.Cameras[0];
        camera.ExposureInput = "0.8";

        var exposure = camera.StartExposureCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => camera.IsExposing && camera.HasActivityProgress, "the exposure");

        Assert.StartsWith("Exposing", camera.ActivityText);
        Assert.True(camera.IsBusy);
        Assert.InRange(camera.ActivityProgress!.Value, 0, 1);

        await exposure;
        Assert.Equal("Idle", camera.ActivityText);
        Assert.Null(camera.ActivityProgress);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task AnInvalidExposureTime_IsAnErrorOfTheCamera_NotAnException(string text)
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var camera = app.Vm.Equipment.Cameras[0];
        camera.ExposureInput = text;

        await camera.StartExposureCommand.ExecuteAsync(null);

        Assert.True(camera.HasError);
        Assert.Contains("seconds", camera.ErrorMessage);
        Assert.False(camera.IsManualExposureRunning);
        Assert.Null(app.Vm.Imaging.LatestFrame);
    }

    [Fact]
    public async Task TheExposureTimeOfACamera_StartsAsTheDemoSaysAndIsEditable()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var camera = app.Vm.Equipment.Cameras[0];

        Assert.Equal("0.1", camera.ExposureInput);
        Assert.Equal("0.1 s", camera.ManualExposureText);

        camera.ExposureInput = "0.2";
        await camera.StartExposureCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromSeconds(0.2), app.Vm.Imaging.LatestFrame!.ExposureDuration);
    }

    [Fact]
    public async Task TheGuiderAndTheMountOfTheSession_SayWhatTheyCanDoAndAreShared()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig, connect: true);

        var guider = app.Vm.Equipment.Guiders[0];
        var mount = app.Vm.Equipment.Mounts[0];

        Assert.Equal("Supported", guider.DitherSupportText);
        Assert.Equal("Supported", guider.SettleSupportText);
        Assert.Equal("Idle", guider.ActivityText);
        Assert.Equal("Idle", mount.ActivityText);

        await guider.StartGuidingCommand.ExecuteAsync(null);
        Assert.Equal("Guiding", guider.ActivityText);
    }

    [Fact]
    public async Task TheFilterWheel_ListsItsSlotsAndTurnsOnRequest()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig, connect: true);
        var wheel = app.Vm.Equipment.FilterWheels[0];

        Assert.Equal(7, wheel.Filters.Count);
        Assert.Equal("L · slot 0", wheel.ActivityText);

        wheel.SelectedFilter = wheel.Filters[4];
        await wheel.ChangeCommand.ExecuteAsync(null);

        Assert.Equal("Ha · slot 4", wheel.ActivityText);
        Assert.Equal("Idle", wheel.MotionText);
    }

    [Fact]
    public async Task WithoutAnyEquipment_ThePageSaysSo_AndRigsRemainOptional()
    {
        var host = new Astra.Runtime.AstraRuntimeHost();
        await using var _ = host;
        using var vm = new MainViewModel(host, action => action());

        Assert.False(vm.Equipment.HasDevices);
        Assert.False(vm.Equipment.HasLandingGroups);
        Assert.True(vm.Equipment.IsLanding);
        Assert.False(vm.Equipment.HasRigs);
        Assert.Empty(vm.Dashboard.Units);
        Assert.False(vm.Dashboard.HasUnits);
    }

    [Fact]
    public async Task EveryDeviceKind_HasADeviceOfItsOwnInTheDetailViews()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);

        var kinds = app.Vm.Equipment.Devices.Select(d => d.GetType()).Distinct().Select(t => t.Name).Order().ToList();

        Assert.Equal(
            ["CameraViewModel", "FilterWheelViewModel", "FocuserViewModel", "GuiderViewModel", "MountViewModel"], kinds);
        Assert.IsAssignableFrom<IDevice>(app.Host.DeviceRegistry.GetAll().OfType<IFocuser>().Single());
    }
}
