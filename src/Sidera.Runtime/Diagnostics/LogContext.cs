using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Diagnostics;

/// <summary>
/// The names of the context values Sidera puts in logging scopes, and a short way to begin a scope with them. A scope
/// lives as long as the thing it names (a sequence execution, a rig track, a coordinated branch, a device operation); it
/// flows into everything that thing starts and never into its siblings, because it follows the async flow, not the thread.
/// </summary>
public static class LogContext
{
    public const string SessionId = nameof(SessionId);
    public const string SequenceExecutionId = nameof(SequenceExecutionId);
    public const string RigId = nameof(RigId);
    public const string TrackId = nameof(TrackId);
    public const string DeviceId = nameof(DeviceId);
    public const string CoordinationGroupId = nameof(CoordinationGroupId);
    public const string ParticipantId = nameof(ParticipantId);

    /// <summary>Begins a scope with the given values; dispose it when the thing it describes ends.</summary>
    public static IDisposable? Begin(this ILogger logger, params (string Key, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var pairs = new KeyValuePair<string, object?>[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            pairs[i] = new KeyValuePair<string, object?>(values[i].Key, values[i].Value);
        }

        return logger.BeginScope(pairs);
    }
}
