namespace Sidera.Runtime.Events;

public sealed record EventHandlerFailure(Type EventType, object Event, Exception Exception);
