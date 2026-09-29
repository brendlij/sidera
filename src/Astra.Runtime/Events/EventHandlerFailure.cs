namespace Astra.Runtime.Events;

public sealed record EventHandlerFailure(Type EventType, object Event, Exception Exception);
