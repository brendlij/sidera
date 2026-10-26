using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Desktop.Notifications;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The messages at the top right: what is shown and for how long, how many at once, how the same message counts instead of stacking, how the pointer holds the clock, and who says what. Time is a fake
/// clock that the test advances, so nothing here waits.
/// </summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private sealed class FakeClock : INotificationScheduler
    {
        private sealed class Entry(DateTimeOffset at, Action callback) : IDisposable
        {
            public DateTimeOffset At { get; } = at;
            public Action Callback { get; } = callback;
            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }

        private readonly List<Entry> _entries = [];

        public DateTimeOffset Now { get; private set; } = new(2026, 10, 12, 22, 0, 0, TimeSpan.Zero);

        public int Pending => _entries.Count(e => !e.Cancelled);

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new Entry(Now + delay, callback);
            _entries.Add(entry);
            return entry;
        }

        public void Advance(TimeSpan time)
        {
            var until = Now + time;
            while (_entries.Where(e => !e.Cancelled && e.At <= until).OrderBy(e => e.At).FirstOrDefault() is { } due)
            {
                Now = due.At;
                _entries.Remove(due);
                due.Callback();
            }

            Now = until;
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-notify-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _apps = [];
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _hosts = [];

    public NotificationTests() => Directory.CreateDirectory(_directory);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }

        foreach (var (host, vm) in _hosts)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private static (NotificationService Service, FakeClock Clock) Make()
    {
        var clock = new FakeClock();
        return (new NotificationService(a => a(), clock), clock);
    }

    private static List<string> Titles(NotificationService service) => service.Visible.Select(n => n.Title).ToList();

    // ---- what is shown

    [Fact]
    public void AMessage_IsShown_WithItsSeverityAndWords_AndTheNewestIsOnTop()
    {
        var (service, _) = Make();

        service.Info("First");
        service.Error("Second", "Because of this");

        Assert.Equal(["Second", "First"], Titles(service));
        var error = service.Visible[0];
        Assert.True(error.IsError);
        Assert.Equal("Error", error.SeverityText);
        Assert.Equal("Because of this", error.Detail);
        Assert.True(error.HasDetail);
        Assert.Equal("Assertive", error.LiveSetting);
        Assert.Equal("Error: Second. Because of this", error.AutomationText);
        Assert.Equal("Polite", service.Visible[1].LiveSetting);
    }

    [Fact]
    public void EachSeverity_HasItsOwnIcon_SoThatAThemeWithOneHueCanStillBeRead()
    {
        var (service, _) = Make();

        service.Success("b");
        service.Warning("c");
        service.Error("d");
        var icons = service.Visible.Select(n => n.IconKey).ToList();
        service.DismissAll();
        service.Info("a");
        icons.Add(service.Visible[0].IconKey);

        Assert.Equal(4, icons.Distinct().Count());
        Assert.Equal(["IconError", "IconWarning", "IconSuccess", "IconInfo"], icons);
    }

    [Fact]
    public void AMessageWithoutWords_IsNotAMessage()
    {
        var (service, _) = Make();

        Assert.Throws<ArgumentException>(() => service.Info("  "));
        Assert.Empty(service.Visible);
    }

    [Fact]
    public void NoMoreThanThreeAreShown_TheOthersWaitTheirTurn_InOrder()
    {
        var (service, _) = Make();

        foreach (var n in new[] { "one", "two", "three", "four", "five" })
        {
            service.Error(n);
        }

        Assert.Equal(["three", "two", "one"], Titles(service));
        service.Dismiss(service.Visible[2]); // "one"
        Assert.Equal(["four", "three", "two"], Titles(service));
        service.Dismiss(service.Visible[0]); // "four"
        Assert.Equal(["five", "three", "two"], Titles(service));
    }

    // ---- how long

    [Theory]
    [InlineData(NotificationSeverity.Info, 4)]
    [InlineData(NotificationSeverity.Success, 4)]
    [InlineData(NotificationSeverity.Warning, 8)]
    public void SuccessAndInfoCloseAfterAboutFourSeconds_AWarningAfterEight(NotificationSeverity severity, int seconds)
    {
        var (service, clock) = Make();
        service.Post(severity, "Something");

        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Single(service.Visible);
        clock.Advance(TimeSpan.FromSeconds(1.1));

        Assert.Empty(service.Visible);
    }

    [Fact]
    public void AnError_StaysUntilItIsClosed()
    {
        var (service, clock) = Make();
        service.Error("It failed");

        clock.Advance(TimeSpan.FromHours(3));

        Assert.Single(service.Visible);
        Assert.Equal(0, clock.Pending);
        service.Dismiss(service.Visible[0]);
        Assert.Empty(service.Visible);
    }

    [Fact]
    public void ClosingAMessage_StopsItsClock_AndAWaitingOneStartsItsOwn()
    {
        var (service, clock) = Make();
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            service.Info(n);
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        service.Dismiss(service.Visible[2]); // "a"
        Assert.Equal(["d", "c", "b"], Titles(service));
        clock.Advance(TimeSpan.FromSeconds(2.1));
        Assert.Equal(["d"], Titles(service)); // "b" and "c" ended after their four seconds; "d" was shown only now and has its own
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(service.Visible);
    }

    // ---- the same message

    [Fact]
    public void TheSameMessage_WhileItIsShown_CountsUp_AndItsClockStartsOver()
    {
        var (service, clock) = Make();
        service.Success("Saved", key: "settings-saved");
        clock.Advance(TimeSpan.FromSeconds(3));

        service.Success("Saved", key: "settings-saved");
        service.Success("Saved", key: "settings-saved");

        var saved = Assert.Single(service.Visible);
        Assert.Equal(3, saved.Count);
        Assert.Equal("×3", saved.CountText);
        Assert.Contains("×3", saved.AutomationText, StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(service.Visible); // four seconds from the last one, not from the first
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(service.Visible);
    }

    [Fact]
    public void WithoutAKey_TheSameWordsAreTheSameMessage()
    {
        var (service, _) = Make();

        service.Warning("Low disk", "2 GB left");
        service.Warning("Low disk", "2 GB left");
        service.Warning("Low disk", "1 GB left");

        Assert.Equal(2, service.Visible.Count);
        Assert.Equal(2, service.Visible.Single(n => n.Detail == "2 GB left").Count);
    }

    [Fact]
    public void TheSameMessage_WhileItWaits_CountsToo_AndDoesNotQueueTwice()
    {
        var (service, _) = Make();
        foreach (var n in new[] { "a", "b", "c" })
        {
            service.Error(n);
        }

        service.Error("d", key: "same");
        service.Error("d", key: "same");
        service.Dismiss(service.Visible[0]);

        var shown = service.Visible.First(n => n.Title == "d");
        Assert.Equal(2, shown.Count);
    }

    // ---- the pointer

    [Fact]
    public void PointingAtAMessage_HoldsItsClock_AndItClosesAFewSecondsAfterThePointerLeft()
    {
        var (service, clock) = Make();
        service.Info("Read me");
        var message = service.Visible[0];
        clock.Advance(TimeSpan.FromSeconds(3));

        message.SetHovered(true);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(service.Visible);

        message.SetHovered(false);
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(service.Visible); // never less than two seconds after the pointer left
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(service.Visible);
    }

    [Fact]
    public void AnErrorIsNotHeld_BecauseItHasNoClock()
    {
        var (service, clock) = Make();
        service.Error("Stays");

        service.Visible[0].SetHovered(true);
        service.Visible[0].SetHovered(false);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Single(service.Visible);
        Assert.Equal(0, clock.Pending);
    }

    // ---- the button, the history

    [Fact]
    public void TheButtonOfAMessage_DoesWhatItSays_AndClosesTheMessage()
    {
        var (service, _) = Make();
        var ran = 0;
        service.Error("Sequence failed", action: new NotificationAction("Open Diagnostics", () => ran++));
        var message = service.Visible[0];

        Assert.True(message.HasAction);
        Assert.Equal("Open Diagnostics", message.ActionLabel);
        message.RunActionCommand.Execute(null);

        Assert.Equal(1, ran);
        Assert.Empty(service.Visible);
    }

    [Fact]
    public void TheHistory_KeepsTheLastFifty_NewestFirst_AndCountsEverySaying()
    {
        var (service, clock) = Make();
        var raised = 0;
        service.HistoryChanged += (_, _) => raised++;

        for (var i = 0; i < 60; i++)
        {
            service.Info("Message " + i, key: "same");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(NotificationService.HistoryLimit, service.History.Count);
        Assert.Equal("Message 59", service.History[0].Title);
        Assert.Equal("Message 10", service.History[^1].Title);
        Assert.Equal(60, raised);
        Assert.True(service.History[0].Time > service.History[1].Time);
    }

    [Fact]
    public void DismissingAll_ClearsTheScreen_AndWhatWaited()
    {
        var (service, clock) = Make();
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            service.Error(n);
        }

        service.DismissAll();

        Assert.Empty(service.Visible);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(service.Visible);
    }

    // ---- who says what

    private MainViewModel Create(Action<SideraRuntimeHost>? devices = null)
    {
        var host = new SideraRuntimeHost();
        devices?.Invoke(host);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        settings.Load();
        var vm = new MainViewModel(
            host, a => a(), new DemoOptions(), withDemoSequence: false,
            equipmentManagement: null, startWithSettingsMode: true, notificationScheduler: new FakeClock());
        _hosts.Add((host, vm));
        return vm;
    }

    [Fact]
    public async Task AFailedMeridianFlip_IsAnErrorMessageWithAWayToTheSession_AndOtherStatesAreNot()
    {
        var host = new SideraRuntimeHost();
        var vm = new MainViewModel(
            host, a => a(), new DemoOptions(), withDemoSequence: false, equipmentManagement: null, startWithSettingsMode: true, notificationScheduler: new FakeClock());
        _hosts.Add((host, vm));

        await host.EventBus.PublishAsync(new Sidera.Core.Mounts.MeridianFlipStateChanged(
            new DeviceId("mount.1"), Sidera.Core.Mounts.MeridianFlipState.Flipping, "Flipping the mount", 0.01, 1, ["Main"]));
        Assert.Empty(vm.Notifications.Visible);

        await host.EventBus.PublishAsync(new Sidera.Core.Mounts.MeridianFlipStateChanged(
            new DeviceId("mount.1"), Sidera.Core.Mounts.MeridianFlipState.Failed, "The flip failed: no solution. Retry the flip or abort the session.", 0.01, 1, ["Main"]));

        var message = Assert.Single(vm.Notifications.Visible);
        Assert.True(message.IsError);
        Assert.Equal("The meridian flip failed", message.Title);
        Assert.Contains("Retry the flip or abort", message.Detail, StringComparison.Ordinal);
        Assert.Equal("Open Session", message.Action?.Label);
        message.Action!.Invoke();
        Assert.Equal(AppPage.Session, vm.SelectedPage);
    }

    [Fact]
    public async Task TheDiagnosticsPage_ListsTheRecentMessages_EvenWhenTheyWereClosed()
    {
        var vm = Create(host => host.AddSimulatedMount(new DeviceId("mount.a"), "EQ6", TimeSpan.FromMilliseconds(20)));
        Assert.False(vm.Diagnostics.HasMessages);

        await vm.Equipment.Mounts.Single().ConnectCommand.ExecuteAsync(null);
        vm.Notifications.DismissAll();

        var row = Assert.Single(vm.Diagnostics.RecentMessages);
        Assert.Equal("EQ6 connected", row.Title);
        Assert.Equal("Success", row.SeverityText);
        Assert.Equal(Sidera.Desktop.ViewModels.StatusKind.Ok, row.Kind);
        Assert.True(vm.Diagnostics.HasMessages);
    }

    [Fact]
    public async Task ConnectingADevice_IsSaid_AndSoIsDisconnecting()
    {
        var vm = Create(host => host.AddSimulatedMount(new DeviceId("mount.a"), "EQ6", TimeSpan.FromMilliseconds(20)));
        var mount = vm.Equipment.Mounts.Single();

        await mount.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(["EQ6 connected"], Titles(vm.Notifications));
        Assert.True(vm.Notifications.Visible[0].IsSuccess);

        await mount.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(["EQ6 disconnected", "EQ6 connected"], Titles(vm.Notifications));
        Assert.True(vm.Notifications.Visible[0].IsInfo);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("No connection could be made.");
    }

    [Fact]
    public async Task ADeviceThatCannotConnect_IsAnErrorThatStays_WithTheReason()
    {
        var vm = Create(host => host.AddDevice(new Sidera.Alpaca.AlpacaDome(
            new DeviceId("dome.bad"), "Roof", new Sidera.Alpaca.AlpacaEndpoint("192.0.2.1", 11111), http: new HttpClient(new Unreachable()))));
        var roof = vm.Equipment.Domes.Single();

        await roof.ConnectCommand.ExecuteAsync(null);

        var error = Assert.Single(vm.Notifications.Visible);
        Assert.True(error.IsError);
        Assert.Equal("Roof could not connect", error.Title);
        Assert.Contains("could not be reached", error.Detail, StringComparison.Ordinal);
        Assert.True(roof.HasError); // and the page of the device says it where the device is
    }

    [Fact]
    public void SavingASettingsTab_IsSaid_AProblemStaysAtTheFields_AndDiscardingIsSaid()
    {
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        settings.Load();
        var service = new NotificationService(a => a(), new FakeClock());
        var tab = new ImagingSettingsViewModel(settings) { Notifications = service };

        tab.ManualExposureText = "0";
        tab.SaveCommand.Execute(null);
        Assert.Empty(service.Visible); // a value that cannot be saved is told next to the field
        Assert.True(tab.ResultIsProblem);

        tab.ManualExposureText = "4";
        tab.SaveCommand.Execute(null);
        Assert.Equal(["Saved"], Titles(service));
        Assert.True(service.Visible[0].IsSuccess);

        tab.ManualExposureText = "9";
        tab.DiscardCommand.Execute(null);
        Assert.Equal(["Changes discarded", "Saved"], Titles(service));
    }

    [Fact]
    public void TheSettingsOfTheWindow_AllTalkToTheSameMessages()
    {
        var vm = Create();
        var settings = vm.Settings;

        Assert.Same(vm.Notifications, settings.Notifications);
        Assert.All(new ViewModelBase?[] { settings.Imaging, settings.Autofocus, settings.Guiding, settings.MeridianFlip, settings.Sequencer, settings.Advanced, settings.Appearance }, tab => Assert.Null(tab));
    }

    [Fact]
    public async Task AShutterThatClosesOrOpens_IsSaid_ButNotTheFirstReadingAfterAConnect()
    {
        var vm = Create(host => host.AddSimulatedDome(new DeviceId("dome.a"), "Roof", shutterOnly: true, shutterTime: TimeSpan.FromMilliseconds(20)));
        var roof = vm.Equipment.Domes.Single();
        await roof.ConnectCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Notifications.Visible, n => n.Title.Contains("shutter", StringComparison.Ordinal));

        await roof.OpenShutterCommand.ExecuteAsync(null);
        Assert.Contains(vm.Notifications.Visible, n => n.Title == "Roof: the shutter is open");

        await roof.CloseShutterCommand.ExecuteAsync(null);
        Assert.Contains(vm.Notifications.Visible, n => n.Title == "Roof: the shutter is closed");
    }
}
