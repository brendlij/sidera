namespace Sidera.Core.Guiding;

/// <summary>
/// Guiding did not settle within the timeout of its <see cref="GuidingSettleOptions"/>. A failure, not a
/// cancellation: guiding itself may still be running, but it never became stable enough.
/// </summary>
public sealed class GuidingSettleTimeoutException(string message) : TimeoutException(message);
