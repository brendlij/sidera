using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Tests.Logging;

/// <summary>One entry as a logger received it: level, template and rendered text, exception, properties and scopes.</summary>
public sealed record CapturedEntry(
    string Category,
    LogLevel Level,
    string Message,
    string Template,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, object?> Scope)
{
    /// <summary>The value of a scope key (the innermost wins), or <c>null</c>.</summary>
    public object? ScopeValue(string key) => Scope.GetValueOrDefault(key);
}

/// <summary>
/// A logger provider for tests: records everything at every level, thread-safe, with the structured properties of the
/// message template and the scopes that were active. Tests wait on entries with <see cref="WaitForAsync"/>, which is
/// signalled by the logger itself, never by sleeping.
/// </summary>
public sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly object _gate = new();
    private readonly List<CapturedEntry> _entries = [];
    private readonly List<(Func<CapturedEntry, bool> Match, TaskCompletionSource<CapturedEntry> Done)> _waiters = [];
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public LogCapture()
    {
        Factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(this));
    }

    public ILoggerFactory Factory { get; }

    public IReadOnlyList<CapturedEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public IEnumerable<CapturedEntry> Where(LogLevel level, string contains) =>
        Entries.Where(e => e.Level == level && e.Message.Contains(contains, StringComparison.Ordinal));

    public IEnumerable<CapturedEntry> Containing(string text) =>
        Entries.Where(e => e.Message.Contains(text, StringComparison.Ordinal));

    public CapturedEntry Single(LogLevel level, string contains) => Where(level, contains).Single();

    /// <summary>Completes when an entry that matches has been logged (also one that was logged before).</summary>
    public Task<CapturedEntry> WaitForAsync(Func<CapturedEntry, bool> match, TimeSpan? timeout = null)
    {
        TaskCompletionSource<CapturedEntry> done;
        lock (_gate)
        {
            var known = _entries.FirstOrDefault(match);
            if (known is not null)
            {
                return Task.FromResult(known);
            }

            done = new TaskCompletionSource<CapturedEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, done));
        }

        return done.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() => Factory.Dispose();

    private void Add(CapturedEntry entry)
    {
        List<TaskCompletionSource<CapturedEntry>> ready = [];
        lock (_gate)
        {
            _entries.Add(entry);
            foreach (var waiter in _waiters.Where(w => w.Match(entry)).ToList())
            {
                _waiters.Remove(waiter);
                ready.Add(waiter.Done);
            }
        }

        foreach (var waiter in ready)
        {
            waiter.TrySetResult(entry);
        }
    }

    private sealed class CapturingLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            var scope = new Dictionary<string, object?>();
            owner._scopes.ForEachScope(
                static (value, target) =>
                {
                    if (value is IEnumerable<KeyValuePair<string, object?>> items)
                    {
                        foreach (var item in items)
                        {
                            target[item.Key] = item.Value;
                        }
                    }
                },
                scope);

            owner.Add(new CapturedEntry(
                category, logLevel, formatter(state, exception),
                properties.TryGetValue("{OriginalFormat}", out var template) ? template?.ToString() ?? string.Empty : string.Empty,
                exception, properties, scope));
        }
    }
}
