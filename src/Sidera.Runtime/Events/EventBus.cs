using Sidera.Core.Events;

namespace Sidera.Runtime.Events;

public sealed class EventBus : IEventPublisher
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<object>> _subscriptions = new();
    private readonly Action<EventHandlerFailure>? _onHandlerFailure;

    /// <param name="onHandlerFailure">
    /// Optional observer, called for every handler that throws. Handler failures never
    /// propagate to the publisher and never stop delivery to the remaining handlers.
    /// </param>
    public EventBus(Action<EventHandlerFailure>? onHandlerFailure = null)
    {
        _onHandlerFailure = onHandlerFailure;
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : ISideraEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription<TEvent>(handler, this);

        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var list))
            {
                list = new List<object>();
                _subscriptions[typeof(TEvent)] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    /// <summary>
    /// Invokes all handlers subscribed to exactly <typeparamref name="TEvent"/>, one after another
    /// in subscription order. Handler exceptions are isolated and reported to the failure observer;
    /// only cancellation of <paramref name="cancellationToken"/> propagates to the caller.
    /// </summary>
    public async Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default)
        where TEvent : ISideraEvent
    {
        ArgumentNullException.ThrowIfNull(sideraEvent);

        Subscription<TEvent>[] snapshot;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var list))
            {
                return;
            }

            snapshot = list.Cast<Subscription<TEvent>>().ToArray();
        }

        foreach (var subscription in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await subscription.Handler(sideraEvent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportFailure(new EventHandlerFailure(typeof(TEvent), sideraEvent, ex));
            }
        }
    }

    private void ReportFailure(EventHandlerFailure failure)
    {
        try
        {
            _onHandlerFailure?.Invoke(failure);
        }
        catch
        {
            // The failure observer must not break publishing either.
        }
    }

    private void Remove<TEvent>(Subscription<TEvent> subscription)
        where TEvent : ISideraEvent
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(typeof(TEvent), out var list))
            {
                list.Remove(subscription);
            }
        }
    }

    private sealed class Subscription<TEvent>(Func<TEvent, CancellationToken, Task> handler, EventBus bus)
        : IDisposable
        where TEvent : ISideraEvent
    {
        public Func<TEvent, CancellationToken, Task> Handler { get; } = handler;

        public void Dispose() => bus.Remove(this);
    }
}
