using Astra.Desktop.ViewModels;

namespace Astra.Desktop.Tests.Ux;

/// <summary>The six pages, and the promise that rigs are optional: nothing says "rig" where there are none.</summary>
public class NavigationAndRigOptionalTests
{
    [Fact]
    public async Task TheSidebar_HasTheWorkAboveTheLineAndTheCareBelowIt_InTheOrderOfTheProduct()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);

        Assert.Equal(
            [AppPage.Dashboard, AppPage.Session, AppPage.Imaging, AppPage.Equipment], app.Vm.PrimaryNavigation.Select(i => i.Page));
        Assert.Equal([AppPage.Diagnostics, AppPage.Settings], app.Vm.SecondaryNavigation.Select(i => i.Page));
        Assert.Equal(
            ["Dashboard", "Session", "Imaging", "Equipment", "Diagnostics", "Settings"],
            app.Vm.PrimaryNavigation.Concat(app.Vm.SecondaryNavigation).Select(i => i.Title));
        Assert.DoesNotContain(app.Vm.PrimaryNavigation.Concat(app.Vm.SecondaryNavigation), i => i.Title == "Sequencer");
        Assert.All(app.Vm.PrimaryNavigation.Concat(app.Vm.SecondaryNavigation), i => Assert.StartsWith("Icon", i.IconKey));
    }

    [Theory]
    [InlineData(AppPage.Dashboard)]
    [InlineData(AppPage.Session)]
    [InlineData(AppPage.Imaging)]
    [InlineData(AppPage.Equipment)]
    [InlineData(AppPage.Diagnostics)]
    [InlineData(AppPage.Settings)]
    public async Task EveryPage_CanBeReachedFromTheSidebar_AndTheCurrentOneIsTheOnlySelectedEntry(AppPage page)
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var item = app.Vm.PrimaryNavigation.Concat(app.Vm.SecondaryNavigation).Single(i => i.Page == page);

        item.Command.Execute(null);

        Assert.Equal(page, app.Vm.SelectedPage);
        Assert.Equal(page.ToString(), app.Vm.PageTitle);
        Assert.Equal([page], app.Vm.PrimaryNavigation.Concat(app.Vm.SecondaryNavigation).Where(i => i.IsSelected).Select(i => i.Page));
        Assert.NotNull(app.Vm.CurrentPage);
    }

    [Fact]
    public async Task TheApplicationStartsOnTheDashboard()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);

        Assert.Equal(AppPage.Dashboard, app.Vm.SelectedPage);
        Assert.True(app.Vm.PrimaryNavigation[0].IsSelected);
        Assert.Same(app.Vm.Dashboard, app.Vm.CurrentPage);
    }

    [Fact]
    public async Task ARuntimeOnline_IsAlwaysSaidAtTheFootOfTheSidebar()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);

        Assert.Equal("Runtime online", app.Vm.Runtime.StatusText);
    }

    // Without any rig

    [Fact]
    public async Task WithoutRigs_TheEquipmentPageShowsDevicesOnly_AndOffersNoRigsMode()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var equipment = app.Vm.Equipment;

        Assert.False(equipment.HasRigs);
        Assert.Empty(equipment.Rigs);
        Assert.True(equipment.HasDevices);
        Assert.Equal(EquipmentMode.Devices, equipment.Mode);
        Assert.Equal(["Cameras", "Mounts", "Guiders"], equipment.Sections.Select(s => s.Title)); // no focusers, no filter wheels: no empty groups
        Assert.Equal(3, equipment.Devices.Count());
    }

    [Fact]
    public async Task WithoutRigs_TheRigsModeFallsBackToDevices()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);

        app.Vm.Equipment.IsRigsMode = true;

        Assert.Equal(EquipmentMode.Devices, app.Vm.Equipment.Mode);
        Assert.True(app.Vm.Equipment.IsDevicesMode);
    }

    [Fact]
    public async Task WithoutRigs_TheDashboardShowsCameras_AndTheWordRigNeverAppears()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var dashboard = app.Vm.Dashboard;

        Assert.False(dashboard.UnitsAreRigs);
        Assert.Equal("Cameras", dashboard.UnitsTitle);
        Assert.All(dashboard.Units, unit => Assert.IsType<CameraViewModel>(unit));
        Assert.False(dashboard.ShowLanes);
    }

    [Fact]
    public async Task WithoutRigs_ASimpleSessionCanBeBuiltAndRun_WithExplicitDeviceSteps()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps(
        [
            new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
            new SlewStepDraft(Guid.NewGuid(), DemoSetup.MountId, 5.5, -5),
            new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1),
            new DitherStepDraft(Guid.NewGuid(), DemoSetup.GuiderId, DemoSetup.MountId, DemoSetup.MainCameraId, 0.6, 0.5, 0.1, 5),
            new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1),
            new StopGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
        ]);
        Assert.True(app.Vm.SequenceDraft.IsValid, string.Join(" ", app.Vm.SequenceDraft.ValidationErrors));

        await app.Vm.Sequencer.RunCommand.ExecuteAsync(null);

        Assert.Equal(Astra.Core.Sequencing.SequenceState.Completed, app.Vm.Sequencer.State);
        Assert.Equal("Completed", app.Vm.Dashboard.StateText);
        Assert.False(app.Vm.Dashboard.ShowLanes); // no Multi-Rig block, so no lanes
    }

    // One rig, several rigs

    [Fact]
    public async Task WithOneRig_TheDevicesAreByKindAndTheRigIsInItsOwnMode()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var equipment = app.Vm.Equipment;

        Assert.True(equipment.HasRigs);
        Assert.Equal(EquipmentMode.Devices, equipment.Mode); // the default
        Assert.Equal(["Cameras", "Focusers", "Filter Wheels", "Mounts", "Guiders"], equipment.Sections.Select(s => s.Title));
        Assert.Single(equipment.Rigs);
        Assert.Single(equipment.Cameras);
        Assert.Single(equipment.Focusers);
        Assert.Single(equipment.FilterWheels);
        Assert.True(app.Vm.Dashboard.UnitsAreRigs);
        Assert.Equal("Rigs", app.Vm.Dashboard.UnitsTitle);
    }

    [Fact]
    public async Task WithSeveralRigs_TheRigsModeListsThemAll_AndTheDeviceGroupsHaveNoRigsInThem()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Assert.Equal(3, equipment.Rigs.Count);

        equipment.IsRigsMode = true;

        Assert.Equal(EquipmentMode.Rigs, equipment.Mode);
        Assert.True(equipment.IsRigsMode);
        Assert.False(equipment.IsDevicesMode);

        equipment.IsDevicesMode = true;

        Assert.Equal(EquipmentMode.Devices, equipment.Mode);
        Assert.All(equipment.Sections, section => Assert.All(section.Items, item => Assert.IsAssignableFrom<DeviceViewModelBase>(item)));
        Assert.DoesNotContain(equipment.Sections, section => section.Title.Contains("Rig", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheMountAndTheGuider_AreDevicesLikeTheOthers_NotSharedEquipment()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var sections = app.Vm.Equipment.Sections;

        Assert.DoesNotContain(sections, s => s.Title.Contains("Shared", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["EQ6 Mount"], sections.Single(s => s.Title == "Mounts").Items.Select(d => d.Name));
        Assert.Equal(["Main Guider"], sections.Single(s => s.Title == "Guiders").Items.Select(d => d.Name));
    }

    [Fact]
    public async Task TheDeviceGroups_AreInTheOrderOfTheKinds_AndEachHoldsOnlyItsKind()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var sections = app.Vm.Equipment.Sections;

        Assert.Equal(["Cameras", "Focusers", "Filter Wheels", "Mounts", "Guiders"], sections.Select(s => s.Title));
        Assert.All(sections[0].Items, d => Assert.IsType<CameraViewModel>(d));
        Assert.All(sections[1].Items, d => Assert.IsType<FocuserViewModel>(d));
        Assert.All(sections[2].Items, d => Assert.IsType<FilterWheelViewModel>(d));
        Assert.All(sections[3].Items, d => Assert.IsType<MountViewModel>(d));
        Assert.All(sections[4].Items, d => Assert.IsType<GuiderViewModel>(d));
        Assert.Equal(app.Vm.Equipment.Devices.Count(), sections.Sum(s => s.Items.Count)); // every device, once
    }
}
