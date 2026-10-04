using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Diagnostics;

/// <summary>One warning or error that was logged: when, how bad, where from, and what it said.</summary>
public sealed record LogProblem(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// <summary>
/// Counts the warnings and errors of this session and keeps the last few, for a diagnostics page that wants to say
/// "2 warnings, 1 error" and show what they were without opening the file. It is a logger provider like the others and
/// stores nothing but those few entries; the log file remains the record. Safe from any thread.
/// </summary>
public sealed class LogSummary : ILoggerProvider
{
    /// <summary>How many of the latest problems are kept.</summary>
    public const int Capacity = 5;

    private readonly object _gate = new();
    private readonly List<LogProblem> _recent = [];
    private int _warnings;
    private int _errors;

    public int WarningCount
    {
        get { lock (_gate) { return _warnings; } }
    }

    /// <summary>Errors and critical entries.</summary>
    public int ErrorCount
    {
        get { lock (_gate) { return _errors; } }
    }

    /// <summary>The latest problems, newest last; at most <see cref="Capacity"/>.</summary>
    public IReadOnlyList<LogProblem> Recent
    {
        get { lock (_gate) { return _recent.ToArray(); } }
    }

    /// <summary>Raised, on the thread that logged, after a warning or an error was counted.</summary>
    public event EventHandler? Changed;

    public ILogger CreateLogger(string categoryName) => new SummaryLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogProblem problem)
    {
        lock (_gate)
        {
            if (problem.Level == LogLevel.Warning)
            {
                _warnings++;
            }
            else
            {
                _errors++;
            }

            _recent.Add(problem);
            if (_recent.Count > Capacity)
            {
                _recent.RemoveAt(0);
            }
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // An observer must not break logging.
        }
    }

    private sealed class SummaryLogger(LogSummary owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Warning or LogLevel.Error or LogLevel.Critical;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var dot = category.LastIndexOf('.');
            owner.Add(new LogProblem(
                DateTimeOffset.Now, logLevel, dot >= 0 ? category[(dot + 1)..] : category, formatter(state, exception)));
        }
    }
}
