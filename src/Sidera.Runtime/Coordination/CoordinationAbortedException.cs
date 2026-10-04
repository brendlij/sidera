namespace Sidera.Runtime.Coordination;

/// <summary>
/// A coordinated operation was called off because a required participant failed before it reached a safe
/// point. It is a cancellation, not a failure of its own: the failure that caused it is reported elsewhere,
/// and a parallel step does not count it as a second error.
/// </summary>
public sealed class CoordinationAbortedException(string message) : OperationCanceledException(message);
