using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The focuser and filter wheel cards, and the rig cards that name them.</summary>
public class EquipmentHardwareTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly DemoOptions Fast = new()
    {
        FocuserStepsPerSecond = 100000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(30),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(30),
    };

    private sealed class App : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public EquipmentViewModel Equipment => Vm.Equipment;

        public FocuserViewModel Focuser(string id) => Equipment.Focusers.Single(f => f.DeviceIdText == id);
        public FilterWheelViewModel Wheel(string id) => Equipment.FilterWheels.Single(w => w.DeviceIdText == id);

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }
    }

    private static App CreateApp(DemoOptions? options = null)
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, options ?? Fast);
        DemoSetup.AddDemoRigs(host, options ?? Fast);
        var gate = new object();
        return new App { Host = host, Vm = new MainViewModel(host, action => { lock (gate) { action(); } }, options ?? Fast) };
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    // The page

    [Fact]
    public async Task TheEquipmentPage_HasACardForEveryFocuserAndFilterWheel_InOrder()
    {
        await using var app = CreateApp();

        Assert.Equal(["focuser.main", "focuser.narrow", "focuser.wide"], app.Equipment.Focusers.Select(f => f.DeviceIdText));
        Assert.Equal(["filterwheel.main", "filterwheel.narrow"], app.Equipment.FilterWheels.Select(w => w.DeviceIdText));
        Assert.Contains(app.Equipment.Devices, d => d is FocuserViewModel);
        Assert.Contains(app.Equipment.Devices, d => d is FilterWheelViewModel);
    }

    // The focuser card

    [Fact]
    public async Task AFocuserCard_ShowsNameIdConnectionPositionAndMotion()
    {
        await using var app = CreateApp();

        var card = app.Focuser("focuser.main");

        Assert.Equal(("Main Focuser", "focuser.main"), (card.Name, card.DeviceIdText));
        Assert.Equal(DeviceConnectionState.Disconnected, card.ConnectionState);
        Assert.Equal("18200 steps", card.PositionText);
        Assert.Equal(FocuserMotionState.Idle, card.MotionState);
        Assert.Equal("0 to 50000 steps", card.RangeText);
        Assert.Equal("18200", card.TargetInput);
    }

    [Fact]
    public async Task OnADisconnectedFocuser_OnlyConnectIsAvailable()
    {
        await using var app = CreateApp();

        var card = app.Focuser("focuser.main");

        Assert.True(card.ConnectCommand.CanExecute(null));
        Assert.False(card.DisconnectCommand.CanExecute(null));
        Assert.False(card.MoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectingAFocuser_MakesMoveAndDisconnectAvailable()
    {
        await using var app = CreateApp();
        var card = app.Focuser("focuser.main");

        await card.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Connected, card.ConnectionState);
        Assert.True(card.MoveCommand.CanExecute(null));
        Assert.True(card.DisconnectCommand.CanExecute(null));
        Assert.False(card.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task AMoveFromTheCard_MovesTheFocuser_AndShowsPositionAndIdleAgain()
    {
        await using var app = CreateApp();
        var card = app.Focuser("focuser.main");
        await card.ConnectCommand.ExecuteAsync(null);
        card.TargetInput = "19500";

        await card.MoveCommand.ExecuteAsync(null);

        Assert.Equal(19500, card.Position);
        Assert.Equal("19500 steps", card.PositionText);
        Assert.Equal(FocuserMotionState.Idle, card.MotionState);
        Assert.False(card.HasError);
        Assert.False(app.Host.ResourceManager.IsHeld(ResourceId.ForDevice(new DeviceId("focuser.main"))));
        var device = (IFocuser)app.Host.DeviceRegistry.GetAll().Single(d => d.Id.Value == "focuser.main");
        Assert.Equal(19500, device.Position);
    }

    [Fact]
    public async Task WhileMoving_TheCardShowsMoving_AndOffersNeitherMoveNorDisconnect()
    {
        // Slow enough to look at: 1000 steps per second.
        await using var app = CreateApp(Fast with { FocuserStepsPerSecond = 1000 });
        var card = app.Focuser("focuser.main");
        await card.ConnectCommand.ExecuteAsync(null);
        card.TargetInput = "19200"; // 1000 steps: one second

        var move = card.MoveCommand.ExecuteAsync(null);
        await WaitUntil(() => card.IsMoving, "the card to show the move");

        Assert.Equal(FocuserMotionState.Moving, card.MotionState);
        Assert.False(card.MoveCommand.CanExecute(null));
        Assert.False(card.DisconnectCommand.CanExecute(null));
        await move.WaitAsync(Bound);
        Assert.False(card.IsMoving);
        Assert.True(card.MoveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("abc", "Focuser position must be a whole number.")]
    [InlineData("1.5", "Focuser position must be a whole number.")]
    [InlineData("99999", "Focuser position must be between 0 and 50000.")]
    [InlineData("-1", "Focuser position must be between 0 and 50000.")]
    public async Task AWrongTarget_IsShownAsASentence_AndNothingMoves(string text, string expected)
    {
        await using var app = CreateApp();
        var card = app.Focuser("focuser.main");
        await card.ConnectCommand.ExecuteAsync(null);
        card.TargetInput = text;

        await card.MoveCommand.ExecuteAsync(null);

        Assert.True(card.HasError);
        Assert.Equal(expected, card.ErrorMessage);
        Assert.Equal(18200, card.Position);
        Assert.True(card.MoveCommand.CanExecute(null)); // and the card can be used again
    }

    [Fact]
    public async Task AMoveClearsTheErrorOfTheLastOne()
    {
        await using var app = CreateApp();
        var card = app.Focuser("focuser.main");
        await card.ConnectCommand.ExecuteAsync(null);
        card.TargetInput = "99999";
        await card.MoveCommand.ExecuteAsync(null);
        Assert.True(card.HasError);

        card.TargetInput = "19000";
        await card.MoveCommand.ExecuteAsync(null);

        Assert.False(card.HasError);
        Assert.Equal(19000, card.Position);
    }

    [Fact]
    public async Task AMoveThatTheRuntimeDoes_ShowsOnTheCardWithoutAnyPolling()
    {
        await using var app = CreateApp();
        var card = app.Focuser("focuser.main");
        await card.ConnectCommand.ExecuteAsync(null);

        // Not from the card: the runtime moves it, the card follows the events.
        await app.Host.DeviceOperations.MoveFocuserToAsync(new DeviceId("focuser.main"), 21000);

        await WaitUntil(() => card.Position == 21000, "the card to show the position");
        Assert.Equal("21000 steps", card.PositionText);
    }

    [Fact]
    public async Task WhileASequenceRuns_NoManualFocuserOrWheelOperationIsOffered()
    {
        await using var app = CreateApp(Fast with { SequenceExposure = TimeSpan.FromSeconds(1), SequenceWait = TimeSpan.FromSeconds(1) });
        foreach (var device in app.Host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        app.Vm.SequenceDraft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 1.5)]);
        var focuser = app.Focuser("focuser.main");
        var wheel = app.Wheel("filterwheel.main");
        focuser.Refresh();
        wheel.Refresh();
        Assert.True(focuser.MoveCommand.CanExecute(null));

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Vm.Sequencer.IsRunning, "the sequence to run");

        Assert.False(focuser.MoveCommand.CanExecute(null));
        Assert.False(focuser.DisconnectCommand.CanExecute(null));
        Assert.False(wheel.ChangeCommand.CanExecute(null));
        Assert.False(wheel.DisconnectCommand.CanExecute(null));
        await run.WaitAsync(Bound);
        Assert.True(focuser.MoveCommand.CanExecute(null));
    }

    // The filter wheel card

    [Fact]
    public async Task AFilterWheelCard_ShowsNameIdFilterAndSlot_ByNameFirst()
    {
        await using var app = CreateApp();

        var card = app.Wheel("filterwheel.main");

        Assert.Equal(("Main Filter Wheel", "filterwheel.main"), (card.Name, card.DeviceIdText));
        Assert.Equal("L", card.CurrentFilterText);
        Assert.Equal("slot 0", card.SlotText);
        Assert.Equal(FilterWheelMotionState.Idle, card.MotionState);
        Assert.Equal(["L", "R", "G", "B", "Ha", "OIII", "SII"], card.Filters.Select(f => f.Name));
        Assert.Equal("L", card.SelectedFilter!.Name);
    }

    [Fact]
    public async Task OnADisconnectedWheel_OnlyConnectIsAvailable_AfterConnectingChangeIs()
    {
        await using var app = CreateApp();
        var card = app.Wheel("filterwheel.main");
        Assert.False(card.ChangeCommand.CanExecute(null));
        Assert.False(card.DisconnectCommand.CanExecute(null));

        await card.ConnectCommand.ExecuteAsync(null);

        Assert.True(card.ChangeCommand.CanExecute(null));
        Assert.True(card.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingHaAndChanging_TurnsTheWheel_AndShowsTheCurrentFilter()
    {
        await using var app = CreateApp();
        var card = app.Wheel("filterwheel.main");
        await card.ConnectCommand.ExecuteAsync(null);

        card.SelectedFilter = card.Filters.Single(f => f.Name == "Ha");
        await card.ChangeCommand.ExecuteAsync(null);

        Assert.Equal("Ha", card.CurrentFilterText);
        Assert.Equal("slot 4", card.SlotText);
        Assert.Equal(FilterWheelMotionState.Idle, card.MotionState);
        var device = (IFilterWheel)app.Host.DeviceRegistry.GetAll().Single(d => d.Id.Value == "filterwheel.main");
        Assert.Equal(new FilterSlot(4, "Ha"), device.CurrentSlot);
        Assert.False(app.Host.ResourceManager.IsHeld(ResourceId.ForDevice(new DeviceId("filterwheel.main"))));
    }

    [Fact]
    public async Task WhileTheWheelTurns_TheCardShowsMoving_AndOffersNeitherChangeNorDisconnect()
    {
        await using var app = CreateApp(Fast with { FilterWheelMoveDuration = TimeSpan.FromMilliseconds(600) });
        var card = app.Wheel("filterwheel.main");
        await card.ConnectCommand.ExecuteAsync(null);
        card.SelectedFilter = card.Filters.Single(f => f.Name == "SII");

        var change = card.ChangeCommand.ExecuteAsync(null);
        await WaitUntil(() => card.IsMoving, "the card to show the change");

        Assert.False(card.ChangeCommand.CanExecute(null));
        Assert.False(card.DisconnectCommand.CanExecute(null));
        await change.WaitAsync(Bound);
        Assert.Equal("SII", card.CurrentFilterText);
        Assert.True(card.ChangeCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheWheelsOfTwoRigsHaveTheirOwnFilters()
    {
        await using var app = CreateApp();

        Assert.Equal(["L", "Ha", "OIII", "SII"], app.Wheel("filterwheel.narrow").Filters.Select(f => f.Name));
        Assert.Equal(7, app.Wheel("filterwheel.main").Filters.Count);
    }

    [Fact]
    public async Task ADisconnectedWheelCannotBeChanged_NoSelectionNeeded()
    {
        await using var app = CreateApp();
        var card = app.Wheel("filterwheel.main");

        card.SelectedFilter = null;

        Assert.False(card.ChangeCommand.CanExecute(null));
    }

    // The rig cards

    [Fact]
    public async Task ARigCard_NamesItsCameraFocuserFilterWheelAndOptics()
    {
        await using var app = CreateApp();

        var rig = app.Equipment.Rigs.Single(r => r.RigIdText == "rig.main");

        Assert.Equal("Main Rig", rig.Name);
        Assert.Equal("Main Camera · camera.main", rig.CameraText);
        Assert.Equal("Main Focuser · focuser.main", rig.FocuserText);
        Assert.Equal("Main Filter Wheel · filterwheel.main", rig.FilterWheelText);
        Assert.Equal("750 mm · f/5 · 3.76 µm pixels", rig.OpticsText);
        Assert.True(rig.HasFocuser);
        Assert.True(rig.HasFilterWheel);
    }

    [Fact]
    public async Task ARigWithoutAFilterWheel_SaysNotConfigured()
    {
        await using var app = CreateApp();

        var wide = app.Equipment.Rigs.Single(r => r.RigIdText == "rig.wide");

        Assert.Equal("Wide Focuser · focuser.wide", wide.FocuserText);
        Assert.Equal("Not configured", wide.FilterWheelText);
        Assert.True(wide.HasFocuser);
        Assert.False(wide.HasFilterWheel);
    }

    [Fact]
    public async Task ACameraOnlyRig_SaysNotConfiguredTwice()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        host.AddRig(new Sidera.Core.Rigs.Rig(
            new Sidera.Core.Rigs.RigId("rig.bare"), "Bare Rig", new DeviceId("camera.main"),
            new Sidera.Core.Rigs.OpticalTrain(500, 100, 3.76, 3.76, 6248, 4176)));
        using var vm = new MainViewModel(host, action => action(), Fast);

        var rig = vm.Equipment.Rigs.Single();

        Assert.Equal(("Not configured", "Not configured"), (rig.FocuserText, rig.FilterWheelText));
        Assert.False(rig.HasFocuser);
        Assert.False(rig.HasFilterWheel);
        await host.DisposeAsync();
    }
}
