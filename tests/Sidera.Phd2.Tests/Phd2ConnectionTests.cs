using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sidera.Phd2.Tests;

/// <summary>The line of communication to PHD2, against the fake server: requests and answers, notifications, and everything that can go wrong.</summary>
public sealed class Phd2ConnectionTests : IAsyncLifetime
{
    private readonly FakePhd2Server _server = new();
    private readonly List<Phd2Connection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _server.DisposeAsync();
    }

    private async Task<Phd2Connection> Connected(Func<Phd2Event, Task>? handler = null)
    {
        var connection = new Phd2Connection(_server.Connector) { EventHandler = handler };
        _connections.Add(connection);
        await connection.ConnectAsync();
        return connection;
    }

    private static async Task<T> Soon<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task ARequest_GetsItsAnswer()
    {
        var connection = await Connected();

        var result = await Soon(connection.CallAsync("get_exposure"));

        Assert.Equal(2000, result!.Value.GetInt32());
        Assert.Equal("get_exposure", Assert.Single(_server.Requests).Method);
    }

    [Fact]
    public async Task TheParametersAreSentAsGiven_AnObjectOrAnArray()
    {
        var connection = await Connected();

        await Soon(connection.CallAsync("dither", new { amount = 1.5, raOnly = true, settle = new { pixels = 1.0, time = 8.0, timeout = 40.0 } }));
        await Soon(connection.CallAsync("set_paused", new object[] { true }));

        var dither = _server.Requests[0].Params!.Value;
        Assert.Equal(1.5, dither.GetProperty("amount").GetDouble());
        Assert.True(dither.GetProperty("raOnly").GetBoolean());
        Assert.Equal(40, dither.GetProperty("settle").GetProperty("timeout").GetDouble());
        Assert.Equal(JsonValueKind.Array, _server.Requests[1].Params!.Value.ValueKind);
        Assert.True(_server.Requests[1].Params!.Value[0].GetBoolean());
    }

    [Fact]
    public async Task SeveralRequestsAtOnce_EachGetItsOwnAnswer_EvenWhenTheAnswersComeOutOfOrder()
    {
        var firstWaits = new TaskCompletionSource();
        _server.On("get_exposure", async _ =>
        {
            await firstWaits.Task;
            return FakeReply.Result(1111);
        });
        _server.On("get_pixel_scale", _ => FakeReply.Result(2.5));
        var connection = await Connected();

        var slow = connection.CallAsync("get_exposure");
        var fast = connection.CallAsync("get_pixel_scale");
        var answerOfFast = await Soon(fast);

        Assert.False(slow.IsCompleted); // the later request was answered first
        Assert.Equal(2.5, answerOfFast!.Value.GetDouble());
        firstWaits.SetResult();
        Assert.Equal(1111, (await Soon(slow))!.Value.GetInt32());
    }

    [Fact]
    public async Task ManyRequestsAtOnce_AreAllAnsweredCorrectly()
    {
        _server.On("get_exposure", request => FakeReply.Result(request.Id));
        var connection = await Connected();

        var calls = Enumerable.Range(0, 100).Select(_ => connection.CallAsync("get_exposure")).ToArray();
        var results = await Soon(Task.WhenAll(calls));

        Assert.Equal(100, results.Select(r => r!.Value.GetInt64()).Distinct().Count());
    }

    [Fact]
    public async Task AnErrorAnswer_IsAnExceptionWithTheCodeAndTheMessageOfPhd2()
    {
        _server.On("set_exposure", _ => FakeReply.Error(1, "could not set exposure duration"));
        var connection = await Connected();

        var error = await Assert.ThrowsAsync<Phd2RpcException>(() => connection.CallAsync("set_exposure", new object[] { 1502 }));

        Assert.Equal((1, "set_exposure", "could not set exposure duration"), (error.Code, error.Method, error.Reason));
        Assert.Contains("could not set exposure duration", error.Message);
    }

    [Fact]
    public async Task AMalformedLine_IsSkipped_AndTheConnectionGoesOn()
    {
        var connection = await Connected();
        await _server.Raw("this is not json");
        await _server.Raw("[1,2,3]");
        await _server.Raw("{\"neither\":\"event nor answer\"}");

        var result = await Soon(connection.CallAsync("get_exposure"));

        Assert.Equal(2000, result!.Value.GetInt32());
        await Until(() => connection.MalformedLines == 3);
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task ANotificationIsHandedOn_WithItsName_AndAnUnknownOneIsNoProblem()
    {
        var seen = new List<string>();
        var connection = await Connected(e =>
        {
            lock (seen)
            {
                seen.Add(e.Name);
            }

            return Task.CompletedTask;
        });

        await _server.Event("StartGuiding");
        await _server.Event("SomethingNewOfAFutureVersion", new { Value = 1 });
        await _server.Event("GuidingStopped");

        await Until(() => { lock (seen) { return seen.Count == 3; } });
        Assert.Equal(["StartGuiding", "SomethingNewOfAFutureVersion", "GuidingStopped"], seen);
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task NotificationsAreHandledInOrder_AndAHandlerThatThrowsDoesNotStopTheNext()
    {
        var seen = new List<double>();
        await Connected(e =>
        {
            if (e.Number("N") == 2)
            {
                throw new InvalidOperationException("a handler that fails");
            }

            lock (seen)
            {
                seen.Add(e.Number("N")!.Value);
            }

            return Task.CompletedTask;
        });

        for (var n = 1; n <= 4; n++)
        {
            await _server.Event("GuideStep", new { N = n });
        }

        await Until(() => { lock (seen) { return seen.Count == 3; } });
        Assert.Equal([1, 3, 4], seen);
    }

    [Fact]
    public async Task ASlowHandler_DoesNotKeepAnAnswerWaiting()
    {
        var release = new TaskCompletionSource();
        var connection = await Connected(async _ => await release.Task);
        await _server.Event("GuideStep");

        var result = await Soon(connection.CallAsync("get_exposure"));

        Assert.Equal(2000, result!.Value.GetInt32());
        release.SetResult();
    }

    [Fact]
    public async Task ACancelledRequest_ThrowsCancellation_AndItsLateAnswerIsIgnored()
    {
        _server.On("get_exposure", _ => FakeReply.Silent);
        var connection = await Connected();
        using var cts = new CancellationTokenSource();
        var call = connection.CallAsync("get_exposure", null, cts.Token);
        await Until(() => _server.Requests.Count == 1);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await _server.Answer(_server.Requests[0].Id, 5);
        _server.On("get_exposure", _ => FakeReply.Result(2000));

        Assert.Equal(2000, (await Soon(connection.CallAsync("get_exposure")))!.Value.GetInt32());
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task ARequestThatIsNotAnswered_TimesOut()
    {
        _server.On("get_exposure", _ => FakeReply.Silent);
        var connection = await Connected();

        await Assert.ThrowsAsync<Phd2TimeoutException>(() => connection.CallAsync("get_exposure", null, default, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task WhenPhd2GoesAway_EveryRequestThatWaitsFails_AndTheEndIsReportedOnce()
    {
        _server.On("get_exposure", _ => FakeReply.Silent);
        var connection = await Connected();
        var closed = 0;
        connection.Closed += _ => Interlocked.Increment(ref closed);
        var first = connection.CallAsync("get_exposure");
        var second = connection.CallAsync("get_exposure");
        await Until(() => _server.Requests.Count == 2);

        _server.Drop();

        await Assert.ThrowsAsync<Phd2ConnectionException>(() => first);
        await Assert.ThrowsAsync<Phd2ConnectionException>(() => second);
        await Until(() => connection.IsClosed);
        Assert.Equal(1, closed);
        await Assert.ThrowsAsync<Phd2ConnectionException>(() => connection.CallAsync("get_exposure"));
    }

    [Fact]
    public async Task ClosingTheConnection_FailsTheRequestsThatWait_AndEndsItsTasks()
    {
        _server.On("get_exposure", _ => FakeReply.Silent);
        var connection = await Connected();
        var pending = connection.CallAsync("get_exposure");
        await Until(() => _server.Requests.Count == 1);
        var closed = 0;
        connection.Closed += _ => Interlocked.Increment(ref closed);

        await connection.DisposeAsync();

        await Assert.ThrowsAsync<Phd2ConnectionException>(() => pending);
        Assert.Equal(0, closed); // a close on purpose is not a loss
        await Assert.ThrowsAsync<Phd2ConnectionException>(() => connection.CallAsync("get_exposure"));
    }

    [Fact]
    public async Task ABurstOfNotifications_IsHandledCompletely_AndInOrder()
    {
        var count = 0;
        var last = 0.0;
        var inOrder = true;
        await Connected(e =>
        {
            var n = e.Number("N")!.Value;
            inOrder &= n == last + 1;
            last = n;
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        for (var n = 1; n <= 5000; n++)
        {
            await _server.Event("GuideStep", new { N = n });
        }

        await Until(() => Volatile.Read(ref count) == 5000);
        Assert.True(inOrder);
    }

    [Fact]
    public async Task ConnectingToNothing_IsAnErrorThatSaysWhereAndWhat()
    {
        var port = FreePort();
        var connection = Phd2Connection.ToEndpoint(new Phd2Endpoint("127.0.0.1", port));
        _connections.Add(connection);

        var error = await Assert.ThrowsAsync<Phd2ConnectionException>(() => connection.ConnectAsync());

        Assert.Contains($"Could not connect to PHD2 at 127.0.0.1:{port}", error.Message);
        Assert.IsType<SocketException>(error.InnerException);
    }

    [Fact]
    public async Task OverARealSocket_ARequestAndANotification_WorkAsOnTheFake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(FakePhd2Server.EventLine("Version", new { PHDVersion = "2.6.13" }) + "\r\n"));
            using var reader = new StreamReader(stream);
            var line = await reader.ReadLineAsync();
            var id = JsonDocument.Parse(line!).RootElement.GetProperty("id").GetInt64();
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"result\":4321,\"id\":{id}}}\r\n"));
            await Task.Delay(200);
        });
        var versions = new TaskCompletionSource<string?>();
        var connection = Phd2Connection.ToEndpoint(new Phd2Endpoint("127.0.0.1", port));
        connection.EventHandler = e =>
        {
            versions.TrySetResult(e.Text("PHDVersion"));
            return Task.CompletedTask;
        };
        _connections.Add(connection);
        await connection.ConnectAsync();

        var result = await Soon(connection.CallAsync("get_exposure"));

        Assert.Equal(4321, result!.Value.GetInt32());
        Assert.Equal("2.6.13", await Soon(versions.Task));
        await serving;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
