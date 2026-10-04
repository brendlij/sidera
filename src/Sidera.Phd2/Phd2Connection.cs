using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Phd2;

/// <summary>
/// The line of communication to one PHD2: requests (JSON-RPC 2.0, one message per line) and the notifications that PHD2 sends on its own,
/// on one stream. Several requests can be outstanding at once; each answer is matched to its request by the id. Notifications are handed
/// on one at a time, in the order they came, by a task of their own, so that a slow handler never keeps an answer waiting. A line that is
/// not understood is logged and skipped. When the stream ends, every request that waits fails and <see cref="Closed"/> is raised once.
/// Nothing here touches the thread of a user interface or blocks a thread.
/// </summary>
internal sealed class Phd2Connection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Phd2Response>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Channel<Phd2Event> _events = Channel.CreateUnbounded<Phd2Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Stream? _stream;
    private Task? _reader;
    private Task? _dispatcher;
    private long _nextId;
    private int _closed;
    private int _malformed;

    public Phd2Connection(Func<CancellationToken, Task<Stream>> connect, ILogger? logger = null)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>A connection over TCP to the event server of PHD2.</summary>
    public static Phd2Connection ToEndpoint(Phd2Endpoint endpoint, ILogger? logger = null, TimeSpan? connectTimeout = null) =>
        new(async ct =>
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(5));
                await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
                return (Stream)new OwnedNetworkStream(client);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                client.Dispose();
                throw new Phd2ConnectionException($"Could not connect to PHD2 at {endpoint}: no answer. Is PHD2 running with its server enabled?");
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                client.Dispose();
                throw new Phd2ConnectionException($"Could not connect to PHD2 at {endpoint}. Is PHD2 running with its server enabled?", ex);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, logger);

    /// <summary>Handles the notifications; set before <see cref="ConnectAsync"/>. A handler that throws is logged and the next one is handled.</summary>
    public Func<Phd2Event, Task>? EventHandler { get; set; }

    /// <summary>The connection ended without <see cref="DisposeAsync"/> having been called: PHD2 closed it or the network failed.</summary>
    public event Action<Exception>? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>How many lines were not understood.</summary>
    public int MalformedLines => Volatile.Read(ref _malformed);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _stream = await _connect(cancellationToken).ConfigureAwait(false);
        _dispatcher = Task.Run(DispatchAsync);
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>Sends a request and waits for its answer.</summary>
    /// <param name="method">The name of the method.</param>
    /// <param name="parameters">Its parameters: an object or an array, or <c>null</c> for none.</param>
    /// <returns>The result of the answer; <c>null</c> when the answer has none.</returns>
    /// <exception cref="Phd2RpcException">PHD2 answered with an error.</exception>
    /// <exception cref="Phd2ConnectionException">The connection is closed or was lost while waiting.</exception>
    /// <exception cref="Phd2TimeoutException">PHD2 did not answer within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; a late answer is ignored.</exception>
    public async Task<JsonElement?> CallAsync(
        string method, object? parameters = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (IsClosed || _stream is null)
        {
            throw new Phd2ConnectionException("The connection to PHD2 is closed.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Phd2Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (IsClosed)
        {
            // Closed between the first check and the registration: the closing did not see this request.
            _pending.TryRemove(id, out _);
            throw new Phd2ConnectionException("The connection to PHD2 is closed.");
        }

        try
        {
            await WriteAsync(Encode(method, parameters, id), cancellationToken).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultRequestTimeout);
            Phd2Response response;
            try
            {
                response = await tcs.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new Phd2TimeoutException($"PHD2 did not answer '{method}' in time.");
            }

            return response.ErrorCode is { } code
                ? throw new Phd2RpcException(method, code, response.ErrorMessage ?? "no message")
                : response.Result;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static byte[] Encode(string method, object? parameters, long id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters);
            }

            writer.WriteNumber("id", id);
            writer.WriteEndObject();
        }

        stream.Write("\r\n"u8);
        return stream.ToArray();
    }

    private async Task WriteAsync(byte[] message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream!.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Close(new Phd2ConnectionException("The connection to PHD2 was lost.", ex), raise: true);
            throw new Phd2ConnectionException("The connection to PHD2 was lost.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(_stream!, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null)
                {
                    Close(new Phd2ConnectionException("PHD2 closed the connection."), raise: true);
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                Handle(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed on purpose.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Close(new Phd2ConnectionException("The connection to PHD2 was lost.", ex), raise: true);
        }
        catch (Exception ex)
        {
            // Nothing in a line may end the reader without everybody that waits being told.
            _logger.LogError(ex, "The reader of the PHD2 connection failed");
            Close(new Phd2ConnectionException("The connection to PHD2 failed.", ex), raise: true);
        }
    }

    private void Handle(string line)
    {
        var message = Phd2Parser.Parse(line, out var problem);
        switch (message)
        {
            case Phd2Response response:
                if (_pending.TryRemove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
                else
                {
                    _logger.LogDebug("PHD2 answered request {Id}, which nobody waits for any more", response.Id);
                }

                break;
            case Phd2Event notification:
                _events.Writer.TryWrite(notification);
                break;
            default:
                Interlocked.Increment(ref _malformed);
                _logger.LogWarning("A line from PHD2 was skipped ({Problem}); it had {Length} characters", problem, line.Length);
                break;
        }
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var notification in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var handler = EventHandler;
                if (handler is null)
                {
                    continue;
                }

                try
                {
                    await handler(notification).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Handling the PHD2 notification {Event} failed", notification.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
    }

    // Ends the connection once: everything that waits fails, the tasks stop, the stream is closed.
    private void Close(Exception reason, bool raise)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _events.Writer.TryComplete();
        try
        {
            _stream?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing the stream to PHD2 failed");
        }

        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                tcs.TrySetException(reason);
            }
        }

        if (raise)
        {
            try
            {
                Closed?.Invoke(reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A handler of the end of the PHD2 connection threw");
            }
        }
    }

    /// <summary>Closes the connection and waits for its tasks. Requests that wait fail with a <see cref="Phd2ConnectionException"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        Close(new Phd2ConnectionException("The connection to PHD2 was closed."), raise: false);
        foreach (var task in new[] { _reader, _dispatcher })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("A task of the PHD2 connection did not end within two seconds");
            }
        }

        _stop.Dispose();
        _writeGate.Dispose();
    }

    // The stream of a TcpClient that closes the client with it.
    private sealed class OwnedNetworkStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
