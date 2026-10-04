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
    public async Task WithoutRigs_TheEquipmentPageHasOnlyTheStandaloneContext_AndNoRigTabs()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var equipment = app.Vm.Equipment;

        Assert.False(equipment.HasRigs);
        Assert.Empty(equipment.Rigs);
        Assert.True(equipment.HasDevices);
        Assert.Equal(["Standalone"], equipment.Contexts.Select(c => c.Title));
        Assert.Equal(["Devices"], equipment.LandingGroups.Select(g => g.Title)); // not "Standalone devices": there is nothing else
        Assert.Equal(3, equipment.Devices.Count());

        equipment.Contexts[0].SelectCommand.Execute(null);

        Assert.Equal(["Camera", "Mount", "Guider"], equipment.Pages.Select(p => p.Title)); // no focusers, no filter wheels: no empty pages
    }

    [Fact]
    public async Task WithoutRigs_NothingAsksForARig_AndEveryDevicePageWorks()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var equipment = app.Vm.Equipment;
        equipment.Contexts[0].SelectCommand.Execute(null);

        foreach (var page in equipment.Pages.ToList())
        {
            page.SelectCommand.Execute(null);
            Assert.NotNull(equipment.SelectedDetail);
            Assert.False(equipment.HasMissingDevice);
        }

        Assert.False(equipment.HasRigs);
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
    public async Task WithOneRig_TheRigIsAContextNextToTheStandaloneDevices()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var equipment = app.Vm.Equipment;

        Assert.True(equipment.HasRigs);
        Assert.True(equipment.IsLanding); // the overview first
        Assert.Equal(["Standalone", "Main Rig"], equipment.Contexts.Select(c => c.Title));
        Assert.Single(equipment.Rigs);
        Assert.Single(equipment.Cameras);
        Assert.Single(equipment.Focusers);
        Assert.Single(equipment.FilterWheels);
        Assert.True(app.Vm.Dashboard.UnitsAreRigs);
        Assert.Equal("Rigs", app.Vm.Dashboard.UnitsTitle);
    }

    [Fact]
    public async Task WithSeveralRigs_EachRigIsAContext_WithItsActualName()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        Assert.Equal(3, equipment.Rigs.Count);

        Assert.Equal(["Standalone", "Main Rig", "Narrow Rig", "Wide Rig"], equipment.Contexts.Select(c => c.Title));

        equipment.Contexts.Single(c => c.Title == "Wide Rig").SelectCommand.Execute(null);

        Assert.True(equipment.IsRigOverview);
        Assert.Equal(["Overview", "Camera", "Focuser"], equipment.Pages.Select(p => p.Title)); // no filter wheel: no such page
        Assert.Equal(["Wide Rig"], equipment.Contexts.Where(c => c.IsSelected).Select(c => c.Title));
    }

    [Fact]
    public async Task TheMountAndTheGuider_AreDevicesLikeTheOthers_NotSharedEquipment_AndNotTheRigs()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;

        // A rig names a camera, a focuser and a filter wheel; the mount and the guider belong to the session and are never a rig page.
        foreach (var rig in equipment.Contexts.Where(c => c.Rig is not null))
        {
            rig.SelectCommand.Execute(null);
            Assert.DoesNotContain(equipment.Pages, p => p.Page is EquipmentPage.Mount or EquipmentPage.Guider);
        }

        equipment.Contexts.Single(c => c.IsStandalone).SelectCommand.Execute(null);
        equipment.Pages.Single(p => p.Page == EquipmentPage.Mount).SelectCommand.Execute(null);
        Assert.Equal("EQ6 Mount", equipment.SelectedDevice!.Name);
        equipment.Pages.Single(p => p.Page == EquipmentPage.Guider).SelectCommand.Execute(null);
        Assert.Equal("Main Guider", equipment.SelectedDevice!.Name);
    }

    [Fact]
    public async Task TheStandalonePages_AreInTheOrderOfTheKinds_AndEachChoosesOnlyAmongItsKind()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var equipment = app.Vm.Equipment;
        equipment.Contexts.Single(c => c.IsStandalone).SelectCommand.Execute(null);

        Assert.Equal(
            [EquipmentPage.Camera, EquipmentPage.Mount, EquipmentPage.Focuser, EquipmentPage.FilterWheel, EquipmentPage.Guider],
            equipment.Pages.Select(p => p.Page));
        var seen = 0;
        foreach (var page in equipment.Pages.ToList())
        {
            page.SelectCommand.Execute(null);
            var kind = equipment.SelectedDevice!.GetType();
            Assert.All(equipment.DeviceChoices, d => Assert.IsType(kind, d));
            seen += equipment.DeviceChoices.Count;
        }

        Assert.Equal(app.Vm.Equipment.Devices.Count(), seen); // every device, once
    }
}
