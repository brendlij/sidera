namespace Astra.Core.Events;

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent astraEvent, CancellationToken cancellationToken = default)
        where TEvent : IAstraEvent;
}
