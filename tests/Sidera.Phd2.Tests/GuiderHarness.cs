using System.Collections.Concurrent;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Guiding;

namespace Sidera.Phd2.Tests;

/// <summary>Records the events a device publishes.</summary>
internal sealed class Recorder : IEventPublisher
{
    private readonly ConcurrentQueue<ISideraEvent> _events = new();

    public IReadOnlyList<ISideraEvent> Events => _events.ToArray();

    public IReadOnlyList<GuidingState> States => _events.OfType<GuidingStateChanged>().Select(e => e.NewState).ToArray();

    public Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default) where TEvent : ISideraEvent
    {
        _events.Enqueue(sideraEvent);
        return Task.CompletedTask;
    }
}

/// <summary>A PHD2 guider against the fake server, with the helpers that tests of it share.</summary>
internal sealed class GuiderHarness : IAsyncDisposable
{
    public static readonly GuidingSettleOptions Settle = new(1.0, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));

    private GuiderHarness(FakePhd2Server server, Phd2Guider guider, Recorder events)
    {
        Server = server;
        Guider = guider;
        Events = events;
    }

    public FakePhd2Server Server { get; }
    public Phd2Guider Guider { get; }
    public Recorder Events { get; }

    public static Phd2GuiderOptions FastOptions => new()
    {
        StartTimeout = TimeSpan.FromSeconds(10),
        StopTimeout = TimeSpan.FromSeconds(2),
        DitherTimeout = TimeSpan.FromSeconds(3),
        SettleGrace = TimeSpan.FromSeconds(1),
        PauseTimeout = TimeSpan.FromSeconds(2),
        RequestTimeout = TimeSpan.FromSeconds(5),
        DefaultSettle = new GuidingSettleOptions(1.5, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)),
    };

    public static GuiderHarness Create(Action<FakePhd2Server>? script = null, Phd2GuiderOptions? options = null)
    {
        var server = new FakePhd2Server();
        script?.Invoke(server);
        var events = new Recorder();
        var guider = new Phd2Guider(
            new DeviceId("guider.main"), "Main Guider", Phd2Endpoint.Default, events, options: options ?? FastOptions, connector: server.Connector);
        return new GuiderHarness(server, guider, events);
    }

    public static async Task<GuiderHarness> ConnectedAsync(Action<FakePhd2Server>? script = null, Phd2GuiderOptions? options = null)
    {
        var harness = Create(script, options);
        await harness.Guider.ConnectAsync();
        return harness;
    }

    /// <summary>A connected guider whose PHD2 is already guiding.</summary>
    public static async Task<GuiderHarness> GuidingAsync(Action<FakePhd2Server>? script = null, Phd2GuiderOptions? options = null)
    {
        var harness = await ConnectedAsync(server =>
        {
            server.On("get_app_state", _ => FakeReply.Result("Guiding"));
            script?.Invoke(server);
        }, options);
        await harness.UntilState(GuidingState.Guiding);
        return harness;
    }

    public async Task UntilState(GuidingState state) => await Until(() => Guider.GuidingState == state, $"the guider to be {state} (it is {Guider.GuidingState})");

    public static async Task Until(Func<bool> condition, string what = "a condition")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(2);
        }
    }

    public static async Task<T> Soon<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    public static async Task Soon(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>A guide step of PHD2 with the errors in pixels.</summary>
    public Task Step(double? ra, double? dec, object? more = null)
    {
        var attributes = new Dictionary<string, object?> { ["Frame"] = 1, ["Time"] = 1.0, ["Mount"] = "AM3" };
        if (ra is { } r)
        {
            attributes["RADistanceRaw"] = r;
        }

        if (dec is { } d)
        {
            attributes["DECDistanceRaw"] = d;
        }

        if (more is not null)
        {
            foreach (var property in System.Text.Json.JsonSerializer.SerializeToElement(more).EnumerateObject())
            {
                attributes[property.Name] = property.Value.Clone();
            }
        }

        return Server.Event("GuideStep", attributes);
    }

    public async ValueTask DisposeAsync()
    {
        await Guider.DisposeAsync();
        await Server.DisposeAsync();
    }
}
