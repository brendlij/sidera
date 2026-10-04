using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Diagnostics;

/// <summary>
/// Writes log entries as lines of text to a file of this session in the log folder: <c>sidera-2026-10-03-221530.log</c>,
/// named after the start of the session, so that one run is one file that can go into a bug report. A session file that
/// gets bigger than <see cref="LoggingOptions.MaxFileBytes"/> is continued in <c>…-2.log</c>, and so on; files that were
/// not written for <see cref="LoggingOptions.RetentionDays"/> days are deleted when the provider starts.
/// <para>
/// Safe from any thread: callers only format their entry and put it in a queue; one background writer owns the file.
/// A caller never waits for the disk. If the queue is full (the disk is stuck) entries are dropped and counted, and the
/// writer says how many once it can write again. Disposing writes everything that was queued and closes the file.
/// </para>
/// <para>
/// Line format: <c>time level category: message | Scope=value …</c>; an exception follows on the next lines, with stack
/// trace and inner exceptions. The scope values are the structured context of the entry (session, sequence execution,
/// rig, device, coordination group), the session id first.
/// </para>
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const int QueueCapacity = 16384;

    private readonly LoggingOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _writer;
    private readonly string _baseName;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private long _dropped;
    private int _disposed;

    /// <param name="time">The clock of the file name and of the entries; the system clock by default.</param>
    public RollingFileLoggerProvider(LoggingOptions options, TimeProvider? time = null)
    {
        _options = options.Validate();
        _time = time ?? TimeProvider.System;

        Directory.CreateDirectory(_options.LogDirectory);
        var started = _time.GetLocalNow();
        _baseName = $"sidera-{started.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}";
        DeleteOldFiles(started);
        CurrentFilePath = ChooseFilePath(1);

        // The file exists before the first entry, so that "where is the log" always has an answer.
        using (File.Open(CurrentFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
        }

        _writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>The file the session is writing to now (the first one until a file gets full).</summary>
    public string CurrentFilePath { get; private set; }

    /// <summary>The entries that were dropped because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _writer.GetAwaiter().GetResult();
        }
        catch
        {
            // A log that cannot be written must never take the application down at exit.
        }
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private string ChooseFilePath(int part)
    {
        var path = Path.Combine(_options.LogDirectory, part == 1 ? $"{_baseName}.log" : $"{_baseName}-{part}.log");

        // Two instances that start in the same second must not share a file.
        var candidate = path;
        for (var n = 2; part == 1 && File.Exists(candidate) && new FileInfo(candidate).Length > 0; n++)
        {
            candidate = Path.Combine(_options.LogDirectory, $"{_baseName}-i{n}.log");
        }

        return candidate;
    }

    private void DeleteOldFiles(DateTimeOffset now)
    {
        var limit = now.UtcDateTime - TimeSpan.FromDays(_options.RetentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_options.LogDirectory, "sidera-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < limit)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // In use by another instance, or already gone: not worth stopping for.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private void Enqueue(string text)
    {
        if (!_queue.Writer.TryWrite(text) && Volatile.Read(ref _disposed) == 0)
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteLoop()
    {
        var part = 1;
        StreamWriter? writer = null;
        long size = 0;
        long reportedDropped = 0;

        StreamWriter Open()
        {
            var stream = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            size = stream.Length;
            return new StreamWriter(stream, new UTF8Encoding(false));
        }

        try
        {
            writer = Open();
            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var text))
                {
                    var dropped = Interlocked.Read(ref _dropped);
                    if (dropped != reportedDropped)
                    {
                        var note = Format(
                            _time.GetLocalNow(), LogLevel.Warning, "FileLog",
                            $"{dropped - reportedDropped} log entries were dropped because the log could not be written fast enough",
                            null, []);
                        reportedDropped = dropped;
                        await writer.WriteAsync(note);
                        size += note.Length;
                    }

                    if (size > _options.MaxFileBytes)
                    {
                        await writer.DisposeAsync();
                        CurrentFilePath = ChooseFilePath(++part);
                        writer = Open();
                    }

                    await writer.WriteAsync(text);
                    size += text.Length;
                }

                await writer.FlushAsync();
            }
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync();
                }
                finally
                {
                    await writer.DisposeAsync();
                }
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    // One entry as text. The context is the key/value pairs of every scope that was begun, outermost first.
    internal static string Format(
        DateTimeOffset time, LogLevel level, string category, string message, Exception? exception,
        IReadOnlyList<KeyValuePair<string, object?>> context)
    {
        var text = new StringBuilder(160);
        text.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
            .Append(LevelText(level)).Append(' ').Append(category).Append(": ")
            .Append(message.Replace("\r\n", "\n").Replace("\n", "\n    "));

        // A key that was begun twice (an operation inside the scope of the same device) appears once, with the innermost value.
        var distinct = new List<KeyValuePair<string, object?>>();
        foreach (var pair in context)
        {
            var existing = distinct.FindIndex(p => p.Key == pair.Key);
            if (existing >= 0)
            {
                distinct[existing] = pair;
            }
            else
            {
                distinct.Add(pair);
            }
        }

        if (distinct.Count > 0)
        {
            text.Append(" | ");
            var first = true;

            // The session first, then the rest as it was begun.
            foreach (var pair in distinct.Where(p => p.Key == "SessionId").Concat(distinct.Where(p => p.Key != "SessionId")))
            {
                if (!first)
                {
                    text.Append(' ');
                }

                first = false;
                text.Append(pair.Key).Append('=').Append(Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
            }
        }

        text.Append('\n');
        if (exception is not null)
        {
            text.Append("    ").Append(exception.ToString().Replace("\r\n", "\n").Replace("\n", "\n    ")).Append('\n');
        }

        return text.ToString();
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Volatile.Read(ref provider._disposed) == 0;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var context = new List<KeyValuePair<string, object?>>();
            provider._scopes.ForEachScope(
                static (scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        list.AddRange(pairs);
                    }
                },
                context);

            provider.Enqueue(Format(
                provider._time.GetLocalNow(), logLevel, category, formatter(state, exception), exception, context));
        }
    }
}
