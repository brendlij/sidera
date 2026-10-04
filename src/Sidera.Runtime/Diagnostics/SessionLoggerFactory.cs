using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Diagnostics;

/// <summary>
/// One run of the application, as a short id. Every entry a logger of the session writes carries it, so one run can be
/// picked out of a log folder. It is not the id of a sequence document or of a sequence execution.
/// </summary>
public static class SessionIds
{
    public static string New() => Short(Guid.NewGuid());

    /// <summary>The first eight hex digits of an id: short enough to read and to quote in a bug report.</summary>
    public static string Short(Guid id) => id.ToString("N")[..8];
}

/// <summary>
/// Gives every logger it creates the session id as scope, so the runtime's entries carry it without each class having to
/// begin a scope: a scope that was begun at start-up would not reach the callbacks and tasks that start later. It does not
/// own the factory it wraps and does not configure it.
/// </summary>
public sealed class SessionLoggerFactory(ILoggerFactory inner, string sessionId) : ILoggerFactory
{
    private readonly KeyValuePair<string, object?>[] _session = [new("SessionId", sessionId)];

    public string SessionId { get; } = sessionId;

    public ILogger CreateLogger(string categoryName) => new SessionLogger(inner.CreateLogger(categoryName), _session);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
    }

    private sealed class SessionLogger(ILogger inner, KeyValuePair<string, object?>[] session) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            // Added to whatever scopes the caller has begun; the file format lists it first.
            using (inner.BeginScope(session))
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
