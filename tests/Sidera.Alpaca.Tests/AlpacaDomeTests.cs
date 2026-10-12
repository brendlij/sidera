using Sidera.Core.Devices;
using Sidera.Core.Domes;

namespace Sidera.Alpaca.Tests;

/// <summary>The dome over Alpaca, against a fake server: what connecting reads and does not do, what a shutter and a move wait for, what a stop does, and what is said when something is wrong.</summary>
public sealed class AlpacaDomeTests
{
    private static readonly AlpacaDomeOptions Quick = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(15), OperationPollInterval = TimeSpan.FromMilliseconds(5), StartGrace = TimeSpan.FromMilliseconds(150),
        ShutterTimeout = TimeSpan.FromSeconds(5), MoveTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(2), MaxPollFailures = 3,
    };

    private static (AlpacaDome Dome, FakeAlpacaServer Server, RecordingEvents Events) Make(Action<FakeAlpacaServer>? configure = null, AlpacaDomeOptions? options = null)
    {
        var server = new FakeAlpacaServer();
        configure?.Invoke(server);
        var events = new RecordingEvents();
        var dome = new AlpacaDome(new DeviceId("dome.1"), "Observatory dome", new AlpacaEndpoint("192.0.2.10", 11111, 0), events, null, options ?? Quick, new HttpClient(server));
        return (dome, server, events);
    }

    [Fact]
    public async Task Connecting_ReadsWhatTheDomeSupports_AndMovesNothing()
    {
        var (dome, server, _) = Make();

        await dome.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, dome.ConnectionState);
        var capabilities = Assert.IsType<DomeCapabilities>(dome.Capabilities.Value);
        Assert.True(capabilities.CanSetShutter);
        Assert.True(capabilities.CanSetAzimuth);
        Assert.True(capabilities.CanPark);
        Assert.False(capabilities.IsShutterOnly);
        Assert.Equal("Fake Dome", capabilities.Driver.Name);
        Assert.Equal(3, capabilities.Driver.InterfaceVersion);
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        Assert.Equal(180, dome.Azimuth);
        Assert.Equal(["PUT connected"], server.Puts.ToArray()); // nothing but the connection was sent as a command
        Assert.Equal("Alpaca", dome.BackendName);
        Assert.Equal("192.0.2.10:11111/0", dome.DriverId);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task ARoof_HasAShutterAndNoAzimuth_AndTheAzimuthIsNotMadeUp()
    {
        var (dome, _, _) = Make(s => { s.CanSetAzimuth = false; s.CanPark = false; s.CanFindHome = false; });

        await dome.ConnectAsync();

        Assert.True(dome.Capabilities.Value!.IsShutterOnly);
        Assert.Contains(dome.Capabilities.Value.Notes, n => n.Contains("roll-off roof", StringComparison.Ordinal));
        Assert.Null(dome.Azimuth); // not 0
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.SlewToAzimuthAsync(90));
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task ConnectionFailing_LeavesItDisconnected_AndSaysWhy()
    {
        var (dome, server, _) = Make(s => s.Unreachable = true);

        var failure = await Assert.ThrowsAsync<AlpacaCommunicationException>(() => dome.ConnectAsync());

        Assert.Contains("192.0.2.10:11111", failure.Message, StringComparison.Ordinal);
        Assert.Equal(DeviceConnectionState.Disconnected, dome.ConnectionState);
        Assert.False(dome.Capabilities.IsAvailable);

        server.Unreachable = false;
        await dome.ConnectAsync(); // and it can be tried again
        Assert.Equal(DeviceConnectionState.Connected, dome.ConnectionState);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task OpeningTheShutter_WaitsUntilItReportsOpen()
    {
        var (dome, server, events) = Make();
        await dome.ConnectAsync();

        await dome.OpenShutterAsync();

        Assert.Equal(DomeShutterState.Open, dome.ShutterState);
        Assert.Single(server.Puts, p => p == "PUT openshutter");
        Assert.Contains(events.All.OfType<DomeShutterStateChanged>(), e => e.NewState == DomeShutterState.Opening);
        Assert.Contains(events.All.OfType<DomeShutterStateChanged>(), e => e.NewState == DomeShutterState.Open);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task ClosingTheShutter_WaitsUntilItReportsClosed()
    {
        var (dome, server, _) = Make(s => s.ShutterStatus = 0);
        await dome.ConnectAsync();

        await dome.CloseShutterAsync();

        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        Assert.Single(server.Puts, p => p == "PUT closeshutter");
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task AShutterThatIsAlreadyOpen_IsNotToldToOpenAgain()
    {
        var (dome, server, _) = Make(s => s.ShutterStatus = 0);
        await dome.ConnectAsync();

        await dome.OpenShutterAsync();

        Assert.DoesNotContain("PUT openshutter", server.Puts);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task AShutterInTheErrorState_IsNotMoved()
    {
        var (dome, server, _) = Make(s => s.ShutterStatus = 4);
        await dome.ConnectAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => dome.OpenShutterAsync());

        Assert.Contains("error", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PUT openshutter", server.Puts);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task AShutterInTheErrorState_CanBeClosed_BecauseClosedIsTheSafeDirection()
    {
        var (dome, server, _) = Make(s => s.ShutterStatus = 4);
        await dome.ConnectAsync();

        await dome.CloseShutterAsync();

        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
        Assert.Single(server.Puts, p => p == "PUT closeshutter");
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task AShutterThatNeverStarts_IsReported_NotWaitedForForever()
    {
        var (dome, _, _) = Make(s => s.ShutterIgnoresOrders = true);
        await dome.ConnectAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => dome.OpenShutterAsync());

        Assert.Contains("did not start", failure.Message, StringComparison.Ordinal);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task ADomeThatCannotSetItsShutter_RefusesToOpenIt()
    {
        var (dome, server, _) = Make(s => s.CanSetShutter = false);
        await dome.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.OpenShutterAsync());

        Assert.DoesNotContain("PUT openshutter", server.Puts);
        Assert.Equal(DomeShutterState.Unknown, dome.ShutterState);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task CancellingAnOpeningShutter_TellsTheDomeToStop()
    {
        var (dome, server, _) = Make(s => s.ShutterMoveReads = 10_000);
        await dome.ConnectAsync();
        using var cancel = new CancellationTokenSource();

        var opening = dome.OpenShutterAsync(cancel.Token);
        await WaitUntil(() => server.Puts.Contains("PUT openshutter"));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Contains("PUT abortslew", server.Puts);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task CancellingWhenTheDomeCannotBeToldToStop_SaysSo()
    {
        var (dome, server, _) = Make(s => s.ShutterMoveReads = 10_000);
        await dome.ConnectAsync();
        using var cancel = new CancellationTokenSource();

        var opening = dome.OpenShutterAsync(cancel.Token);
        await WaitUntil(() => server.Puts.Contains("PUT openshutter"));
        server.Errors["abortslew"] = 0x40B;
        cancel.Cancel();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
        Assert.Contains("may still be moving", failure.Message, StringComparison.Ordinal);
        server.Errors.Clear();
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task Halt_StopsARunningOperation_AndItEndsAsCancelled()
    {
        var (dome, server, _) = Make(s => s.ShutterMoveReads = 10_000);
        await dome.ConnectAsync();

        var opening = dome.OpenShutterAsync();
        await WaitUntil(() => server.Puts.Contains("PUT openshutter"));
        await dome.HaltAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Contains("PUT abortslew", server.Puts);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task OnlyOneOperationRunsAtATime()
    {
        var (dome, server, _) = Make(s => s.ShutterMoveReads = 10_000);
        await dome.ConnectAsync();
        using var cancel = new CancellationTokenSource();
        var opening = dome.OpenShutterAsync(cancel.Token);
        await WaitUntil(() => server.Puts.Contains("PUT openshutter"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => dome.CloseShutterAsync());
        var disconnect = await Assert.ThrowsAsync<InvalidOperationException>(() => dome.DisconnectAsync());

        Assert.Contains("busy", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Cannot disconnect", disconnect.Message, StringComparison.Ordinal);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task TurningTheDome_SendsTheAzimuthAsZeroTo360_AndWaitsForTheEndOfTheMove()
    {
        var (dome, server, _) = Make();
        await dome.ConnectAsync();

        await dome.SlewToAzimuthAsync(370.5);

        Assert.Equal(10.5, server.Azimuth);
        Assert.Equal(10.5, dome.Azimuth);
        Assert.False(dome.IsMoving);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task AnAzimuthThatIsNotANumber_IsRefused_BeforeAnythingIsSent()
    {
        var (dome, server, _) = Make();
        await dome.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dome.SlewToAzimuthAsync(double.NaN));

        Assert.DoesNotContain("PUT slewtoazimuth", server.Puts);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task Parking_WaitsUntilTheDomeIsAtPark()
    {
        var (dome, server, _) = Make();
        await dome.ConnectAsync();

        await dome.ParkAsync();

        Assert.True(dome.Telemetry!.AtPark);
        Assert.Single(server.Puts, p => p == "PUT park");
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task AParkThatEndsWithoutArriving_IsAFailure()
    {
        var (dome, _, _) = Make(s => s.MovesDoNotArrive = true);
        await dome.ConnectAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => dome.ParkAsync());

        Assert.Contains("without arriving", failure.Message, StringComparison.Ordinal);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task FindingHome_WaitsUntilTheDomeIsHome()
    {
        var (dome, _, _) = Make();
        await dome.ConnectAsync();

        await dome.FindHomeAsync();

        Assert.True(dome.Telemetry!.AtHome);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task ADomeThatCannotPark_RefusesWithoutSendingAnything()
    {
        var (dome, server, _) = Make(s => s.CanPark = false);
        await dome.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.ParkAsync());

        Assert.DoesNotContain("PUT park", server.Puts);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task WhatTheServerRefuses_IsSaidInItsOwnWords()
    {
        var (dome, server, _) = Make();
        await dome.ConnectAsync();
        server.Errors["slewtoazimuth"] = 0x401;

        var failure = await Assert.ThrowsAsync<AlpacaException>(() => dome.SlewToAzimuthAsync(90));

        Assert.Equal(0x401, failure.ErrorNumber);
        Assert.Contains("slewtoazimuth fails", failure.Message, StringComparison.Ordinal);
        server.Errors.Clear();
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task OperationsNeedAConnection()
    {
        var (dome, server, _) = Make();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.OpenShutterAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dome.HaltAsync());

        Assert.Empty(server.Snapshot());
    }

    [Fact]
    public async Task AShutterThatClosesByItself_IsSeenWhileConnected()
    {
        var (dome, server, events) = Make(s => s.ShutterStatus = 0);
        await dome.ConnectAsync();
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);

        server.ShutterStatus = 1; // the rain sensor of the roof

        await WaitUntil(() => dome.ShutterState == DomeShutterState.Closed);
        Assert.Contains(events.All.OfType<DomeShutterStateChanged>(), e => e is { PreviousState: DomeShutterState.Open, NewState: DomeShutterState.Closed });
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task ADomeThatStopsAnswering_IsReportedAsFaulted_AndCanBeConnectedAgain()
    {
        var (dome, server, events) = Make();
        await dome.ConnectAsync();

        server.Unreachable = true;

        await WaitUntil(() => dome.ConnectionState == DeviceConnectionState.Faulted);
        Assert.Contains(events.All.OfType<DeviceConnectionStateChanged>(), e => e.NewState == DeviceConnectionState.Faulted);

        server.Unreachable = false;
        await dome.ConnectAsync();
        Assert.Equal(DeviceConnectionState.Connected, dome.ConnectionState);
        await dome.DisconnectAsync();
    }

    [Fact]
    public async Task Disconnecting_TellsTheDevice_AndForgetsWhatItKnew()
    {
        var (dome, server, _) = Make();
        await dome.ConnectAsync();

        await dome.DisconnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, dome.ConnectionState);
        Assert.False(dome.Capabilities.IsAvailable);
        Assert.Null(dome.Telemetry);
        Assert.Equal(2, server.Puts.Count(p => p == "PUT connected"));
        Assert.False(server.Connected);
    }

    [Fact]
    public async Task EveryRequest_GoesToTheAddressOfTheDome_AndToNoOther()
    {
        var (dome, server, _) = Make();

        await dome.ConnectAsync();
        await dome.OpenShutterAsync();
        await dome.DisconnectAsync();

        Assert.NotEmpty(server.Uris);
        Assert.All(server.Uris, uri =>
        {
            Assert.Equal("192.0.2.10", uri.Host);
            Assert.Equal(11111, uri.Port);
            Assert.StartsWith("/api/v1/dome/0/", uri.AbsolutePath, StringComparison.Ordinal);
        });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
