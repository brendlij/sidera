namespace Sidera.Core.Events;

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default)
        where TEvent : ISideraEvent;
}
