using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Mounts;
using Sidera.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Tests.Logging;

public class DeviceLoggingTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(5);

    private sealed class FailingDevice : IDevice
    {
        public DeviceId Id { get; } = new("camera.broken");
        public string Name => "Broken Camera";
        public DeviceType Type => DeviceType.Camera;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("USB device not found"));
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static (SideraRuntimeHost Host, LogCapture Log) Create()
    {
        var log = new LogCapture();
        return (new SideraRuntimeHost(loggerFactory: log.Factory), log);
    }

    [Fact]
    public async Task ConnectingAndDisconnecting_AreInformation_WithTheDevice()
    {
        var (host, log) = Create();
        await using var _ = host;
        var focuser = host.AddSimulatedFocuser(new DeviceId("focuser.main"), "Main Focuser", 1000);

        await host.DeviceOperations.ConnectAsync(focuser.Id);
        await host.DeviceOperations.DisconnectAsync(focuser.Id);

        var connecting = log.Single(LogLevel.Information, "Focuser focuser.main (Main Focuser) connecting");
        var connected = log.Single(LogLevel.Information, "Focuser focuser.main (Main Focuser) connected");
        log.Single(LogLevel.Information, "Focuser focuser.main (Main Focuser) disconnecting");
        var disconnected = log.Single(LogLevel.Information, "Focuser focuser.main (Main Focuser) disconnected");
        Assert.Equal("focuser.main", connecting.ScopeValue(LogContext.DeviceId));
        Assert.Equal("Focuser", connected.Properties["DeviceType"]?.ToString());
        Assert.Equal("Main Focuser", connected.Properties["DeviceName"]);
        Assert.Equal(host.SessionId, disconnected.ScopeValue(LogContext.SessionId));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AFailedConnect_IsAnErrorWithTheException()
    {
        var (host, log) = Create();
        await using var _ = host;
        host.AddDevice(new FailingDevice());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.ConnectAsync(new DeviceId("camera.broken")));

        var error = log.Single(LogLevel.Error, "Connect on camera.broken failed");
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal("USB device not found", error.Exception!.Message);
        Assert.Equal("camera.broken", error.ScopeValue(LogContext.DeviceId));
    }

    [Fact]
    public async Task ADeviceThatDisconnectsOnItsOwn_IsAWarning_AndAFaultedOneAnError()
    {
        var (host, log) = Create();
        await using var _ = host;
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var bus = host.EventBus;

        await bus.PublishAsync(new DeviceConnectionStateChanged(camera.Id, DeviceConnectionState.Connected, DeviceConnectionState.Disconnected));
        await bus.PublishAsync(new DeviceConnectionStateChanged(camera.Id, DeviceConnectionState.Connecting, DeviceConnectionState.Faulted));

        var warning = log.Single(LogLevel.Warning, "disconnected unexpectedly");
        Assert.Equal("camera.main", warning.ScopeValue(LogContext.DeviceId));
        log.Single(LogLevel.Error, "faulted");
    }

    [Fact]
    public async Task ManualOperations_LogTheirParameters()
    {
        var (host, log) = Create();
        await using var _ = host;
        var focuser = host.AddSimulatedFocuser(new DeviceId("focuser.main"), "Focuser", 1000, stepsPerSecond: 1_000_000, minimumMoveDuration: Short);
        var wheel = host.AddSimulatedFilterWheel(
            new DeviceId("wheel.main"), "Wheel", [new FilterSlot(0, "L"), new FilterSlot(1, "Ha")], moveDuration: Short);
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");
        var mount = host.AddSimulatedMount(new DeviceId("mount.main"), "Mount", Short);
        foreach (var device in new IDevice[] { focuser, wheel, camera, mount })
        {
            await host.DeviceOperations.ConnectAsync(device.Id);
        }

        await host.DeviceOperations.MoveFocuserToAsync(focuser.Id, 2500);
        await host.DeviceOperations.MoveFilterWheelToAsync(wheel.Id, 1);
        await host.DeviceOperations.ExposeAsync(camera.Id, TimeSpan.FromMilliseconds(20));
        await host.DeviceOperations.SlewToAsync(mount.Id, new CelestialCoordinates(5.5, 12.25));

        var move = log.Single(LogLevel.Information, "Moving focuser focuser.main");
        Assert.Equal(1000, move.Properties["FromPosition"]);
        Assert.Equal(2500, move.Properties["TargetPosition"]);
        Assert.Equal(1, log.Single(LogLevel.Information, "Turning filter wheel").Properties["SlotIndex"]);
        Assert.Equal("0.02", log.Single(LogLevel.Information, "Exposing camera").Properties["ExposureSeconds"]);
        var slew = log.Single(LogLevel.Information, "Slewing mount");
        Assert.Equal(5.5, slew.Properties["RightAscensionHours"]);
        Assert.Equal(12.25, slew.Properties["DeclinationDegrees"]);

        // The completion, with its duration, is Debug; the operation's own device is its context.
        var done = log.Entries.Single(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Focuser move on focuser.main completed"));
        Assert.Equal("focuser.main", done.ScopeValue(LogContext.DeviceId));
        Assert.IsType<double>(done.Properties["DurationMs"]);
    }

    [Fact]
    public async Task ACancelledManualOperation_IsInformation_NotAnError()
    {
        var (host, log) = Create();
        await using var _ = host;
        var focuser = host.AddSimulatedFocuser(new DeviceId("focuser.main"), "Focuser", 0, stepsPerSecond: 10);
        await host.DeviceOperations.ConnectAsync(focuser.Id);
        using var cts = new CancellationTokenSource();

        var move = host.DeviceOperations.MoveFocuserToAsync(focuser.Id, 40000, cts.Token);
        await log.WaitForAsync(e => e.Message.StartsWith("Resources", StringComparison.Ordinal) && e.Message.Contains("granted"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        log.Single(LogLevel.Information, "Focuser move on focuser.main was cancelled");
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TheRuntimeLogsItsStartAndItsStop()
    {
        var (host, log) = Create();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");
        host.Start();
        await host.DeviceOperations.ConnectAsync(camera.Id);

        await host.StopAsync();
        await host.DisposeAsync();

        log.Single(LogLevel.Information, "Runtime started with 1 devices");
        log.Single(LogLevel.Information, "Runtime stopping");
        log.Single(LogLevel.Information, "Runtime stopped");
        Assert.Contains(log.Entries, e => e.Message.Contains("camera.main") && e.Message.Contains("disconnected"));
    }

    [Fact]
    public async Task TheSessionId_IsOnEveryEntryOfTheRuntime_AndDiffersBetweenSessions()
    {
        var (host, log) = Create();
        var (other, otherLog) = Create();
        await using var _ = host;
        await using var __ = other;
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");
        other.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");

        await host.DeviceOperations.ConnectAsync(new DeviceId("camera.main"));
        await other.DeviceOperations.ConnectAsync(new DeviceId("camera.main"));

        Assert.NotEqual(host.SessionId, other.SessionId);
        Assert.Matches("^[0-9a-f]{8}$", host.SessionId);
        Assert.NotEmpty(log.Entries);
        Assert.All(log.Entries, e => Assert.Equal(host.SessionId, e.ScopeValue(LogContext.SessionId)));
        Assert.All(otherLog.Entries, e => Assert.Equal(other.SessionId, e.ScopeValue(LogContext.SessionId)));
    }

    [Fact]
    public async Task StateChanges_AreOnlyTrace()
    {
        var (host, log) = Create();
        await using var _ = host;
        var focuser = host.AddSimulatedFocuser(new DeviceId("focuser.main"), "Focuser", 1000, stepsPerSecond: 1_000_000, minimumMoveDuration: Short);
        await host.DeviceOperations.ConnectAsync(focuser.Id);

        await host.DeviceOperations.MoveFocuserToAsync(focuser.Id, 2000);

        var states = log.Entries.Where(e => e.Category.EndsWith("StateStore", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(states);
        Assert.All(states, e => Assert.Equal(LogLevel.Trace, e.Level));
        Assert.Contains(states, e => e.Message.Contains("connection Connecting -> Connected"));
        Assert.Contains(states, e => e.Message.Contains("focuser position 2000"));
    }

    [Fact]
    public async Task AHostWithoutALoggerFactory_WorksAndLogsNothingAnywhere()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");

        await host.DeviceOperations.ConnectAsync(camera.Id);
        await host.DeviceOperations.ExposeAsync(camera.Id, Short);

        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
    }

    [Fact]
    public async Task ASessionIdCanBeGiven()
    {
        await using var host = new SideraRuntimeHost(sessionId: "feedc0de");

        Assert.Equal("feedc0de", host.SessionId);
    }
}
