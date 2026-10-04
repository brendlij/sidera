using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Sidera.Phd2.Tests;

/// <summary>A request the fake server received: its id, the method and the parameters as the client sent them.</summary>
internal sealed record FakeRequest(long Id, string Method, JsonElement? Params, string Raw)
{
    public double Number(string name) => Params!.Value.GetProperty(name).GetDouble();
}

/// <summary>What the fake server answers: a result, an error, or nothing (a request that is answered by hand later).</summary>
internal abstract record FakeReply
{
    public static FakeReply Result(object? value = null) => new ResultReply(value ?? 0);
    public static FakeReply Error(int code, string message) => new ErrorReply(code, message);
    public static FakeReply Silent { get; } = new SilentReply();

    internal sealed record ResultReply(object Value) : FakeReply;
    internal sealed record ErrorReply(int Code, string Message) : FakeReply;
    internal sealed record SilentReply : FakeReply;
}

/// <summary>
/// A stand-in for PHD2 in memory: it speaks the protocol of the event server (one JSON message per line) over a pair of pipes, so a test
/// needs neither PHD2 nor a socket. Requests are handled concurrently, so an answer can be late or out of order. Every <see cref="Connect"/>
/// is a new session, like a new connection to PHD2. A test scripts the answers with <see cref="On"/> and sends notifications with
/// <see cref="Event"/>.
/// </summary>
internal sealed class FakePhd2Server : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<FakeRequest, Task<FakeReply>>> _handlers = new(StringComparer.Ordinal);
    private readonly List<FakeRequest> _requests = [];
    private readonly List<Session> _sessions = [];
    private readonly List<string> _initialLines = [];

    public FakePhd2Server()
    {
        // What a PHD2 with a profile and connected equipment, not doing anything, says.
        On("get_app_state", _ => FakeReply.Result("Stopped"));
        On("get_connected", _ => FakeReply.Result(true));
        On("get_profile", _ => FakeReply.Result(new { id = 1, name = "Main Rig" }));
        On("get_current_equipment", _ => FakeReply.Result(new
        {
            camera = new { name = "ASI120MM Mini", connected = true },
            mount = new { name = "AM3", connected = true },
        }));
        On("get_calibrated", _ => FakeReply.Result(true));
        On("get_exposure", _ => FakeReply.Result(2000));
        On("get_pixel_scale", _ => FakeReply.Result(2.0));
        On("stop_capture", _ => FakeReply.Result());
        On("set_paused", _ => FakeReply.Result());
        On("guide", _ => FakeReply.Result());
        On("dither", _ => FakeReply.Result());
    }

    /// <summary>Every request received, in the order it arrived, over all sessions.</summary>
    public IReadOnlyList<FakeRequest> Requests
    {
        get { lock (_gate) { return _requests.ToArray(); } }
    }

    public IEnumerable<string> Methods => Requests.Select(r => r.Method);

    public int Sessions
    {
        get { lock (_gate) { return _sessions.Count; } }
    }

    /// <summary>The lines PHD2 sends first when a client connects (Version, AppState ...), as they are.</summary>
    public FakePhd2Server InitialLines(params string[] lines)
    {
        _initialLines.Clear();
        _initialLines.AddRange(lines);
        return this;
    }

    public FakePhd2Server On(string method, Func<FakeRequest, FakeReply> handler)
    {
        lock (_gate)
        {
            _handlers[method] = request => Task.FromResult(handler(request));
        }

        return this;
    }

    public FakePhd2Server On(string method, Func<FakeRequest, Task<FakeReply>> handler)
    {
        lock (_gate)
        {
            _handlers[method] = handler;
        }

        return this;
    }

    /// <summary>The connector to give a client: each call is a new session.</summary>
    public Func<CancellationToken, Task<Stream>> Connector => Connect;

    public async Task<Stream> Connect(CancellationToken cancellationToken = default)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        var server = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        var session = new Session(this, server);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        foreach (var line in _initialLines)
        {
            await session.SendAsync(line).ConfigureAwait(false);
        }

        session.Start();
        return client;
    }

    private Session Current
    {
        get
        {
            lock (_gate)
            {
                return _sessions[^1];
            }
        }
    }

    /// <summary>Sends a notification of PHD2 with the attributes common to all, and the given ones.</summary>
    public Task Event(string name, object? attributes = null) => Raw(EventLine(name, attributes));

    public static string EventLine(string name, object? attributes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["Event"] = name,
            ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["Host"] = "TESTHOST",
            ["Inst"] = 1,
        };
        if (attributes is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(attributes).EnumerateObject())
            {
                body[property.Name] = property.Value.Clone();
            }
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>Sends a line exactly as it is (to send what is not JSON, or an answer by hand).</summary>
    public Task Raw(string line) => Current.SendAsync(line);

    /// <summary>The answer to a request that was left unanswered.</summary>
    public Task Answer(long id, object? result = null) =>
        Raw(JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["result"] = result ?? 0, ["id"] = id }));

    /// <summary>PHD2 goes away: the connection of the current session ends.</summary>
    public void Drop() => Current.Close();

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Session(FakePhd2Server owner, Stream stream) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;

        public void Start() => _loop = Task.Run(ReadAsync);

        public async Task SendAsync(string line)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n")).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The client is gone.
            }
            finally
            {
                _write.Release();
            }
        }

        public void Close() => stream.Dispose();

        private async Task ReadAsync()
        {
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var request = Parse(line);
                    lock (owner._gate)
                    {
                        owner._requests.Add(request);
                    }

                    _ = Task.Run(() => HandleAsync(request));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Over.
            }
        }

        private static FakeRequest Parse(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new FakeRequest(
                root.GetProperty("id").GetInt64(),
                root.GetProperty("method").GetString()!,
                root.TryGetProperty("params", out var p) ? p.Clone() : null,
                line);
        }

        private async Task HandleAsync(FakeRequest request)
        {
            Func<FakeRequest, Task<FakeReply>>? handler;
            lock (owner._gate)
            {
                owner._handlers.TryGetValue(request.Method, out handler);
            }

            var reply = handler is null ? FakeReply.Error(-32601, "method not found") : await handler(request).ConfigureAwait(false);
            switch (reply)
            {
                case FakeReply.ResultReply result:
                    await SendAsync(JsonSerializer.Serialize(
                        new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["result"] = result.Value, ["id"] = request.Id })).ConfigureAwait(false);
                    break;
                case FakeReply.ErrorReply error:
                    await SendAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["error"] = new { code = error.Code, message = error.Message },
                        ["id"] = request.Id,
                    })).ConfigureAwait(false);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            stream.Dispose();
            if (_loop is not null)
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
    }

    // Reads from one stream and writes to another: the two ends of a pair of pipes make a connection.
    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
