using System.Net;
using System.Text;
using System.Text.Json;
using Sidera.Core.Events;

namespace Sidera.Alpaca.Tests;

/// <summary>A fake of an Alpaca server with one dome: it answers like the real thing (JSON with Value, ErrorNumber, ErrorMessage) and lets a test say what the dome is and what happens when.</summary>
internal sealed class FakeAlpacaServer : HttpMessageHandler
{
    private readonly object _gate = new();

    public bool Connected { get; set; }
    public bool Unreachable { get; set; }

    // 0 open, 1 closed, 2 opening, 3 closing, 4 error
    public int ShutterStatus { get; set; } = 1;
    public double Azimuth { get; set; } = 180;
    public bool Slewing { get; set; }
    public bool AtPark { get; set; }
    public bool AtHome { get; set; }

    public bool CanSetShutter { get; set; } = true;
    public bool CanSetAzimuth { get; set; } = true;
    public bool CanSetAltitude { get; set; }
    public bool CanPark { get; set; } = true;
    public bool CanFindHome { get; set; } = true;
    public bool CanSlave { get; set; }

    /// <summary>The reads of the shutter status after which a shutter that was told to move has arrived; 0 means at once.</summary>
    public int ShutterMoveReads { get; set; } = 2;

    /// <summary>The shutter ignores the order: it does not start to move.</summary>
    public bool ShutterIgnoresOrders { get; set; }

    /// <summary>The reads of the slewing flag after which a move has ended.</summary>
    public int MoveReads { get; set; } = 2;

    /// <summary>A park or home that ends without arriving.</summary>
    public bool MovesDoNotArrive { get; set; }

    /// <summary>Methods (lower case) that answer with this Alpaca error number instead of working.</summary>
    public Dictionary<string, int> Errors { get; } = [];

    public List<string> Requests { get; } = [];
    public List<Uri> Uris { get; } = [];

    private int _shutterTarget = -1;
    private int _shutterReads;
    private int _moveReads;
    private string? _moveKind;
    private double _slewTarget;

    public IEnumerable<string> Puts => Snapshot().Where(r => r.StartsWith("PUT ", StringComparison.Ordinal));

    public List<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. Requests];
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("No connection could be made.");
        }

        var path = request.RequestUri!.AbsolutePath;
        var method = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        lock (_gate)
        {
            Requests.Add($"{request.Method} {method}");
            Uris.Add(request.RequestUri);
        }

        Dictionary<string, string> form = [];
        if (request.Content is not null)
        {
            foreach (var pair in request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult().Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                form[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }
        }

        string body;
        lock (_gate)
        {
            body = request.Method == HttpMethod.Put ? Put(method, form) : Get(method);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static string Answer(object? value, int error = 0, string message = "") =>
        JsonSerializer.Serialize(value is null ? new { ClientTransactionID = 0, ServerTransactionID = 1, ErrorNumber = error, ErrorMessage = message } as object : new { Value = value, ClientTransactionID = 0, ServerTransactionID = 1, ErrorNumber = error, ErrorMessage = message });

    private string Get(string method)
    {
        if (Errors.TryGetValue(method, out var error))
        {
            return Answer(0, error, $"{method} fails with {error}");
        }

        object? Needs(object value) => Connected ? value : null;
        string NotConnected() => Answer(0, 0x407, "The dome is not connected");
        switch (method)
        {
            case "name": return Answer("Fake Dome");
            case "description": return Answer("A fake dome of the tests");
            case "driverinfo": return Answer("Fake driver");
            case "driverversion": return Answer("1.2");
            case "interfaceversion": return Answer(3);
            case "connected": return Answer(Connected);
            case "cansetshutter": return Answer(CanSetShutter);
            case "cansetazimuth": return Answer(CanSetAzimuth);
            case "cansetaltitude": return Answer(CanSetAltitude);
            case "canpark": return Answer(CanPark);
            case "cansetpark": return Answer(false);
            case "canfindhome": return Answer(CanFindHome);
            case "cansyncazimuth": return Answer(false);
            case "canslave": return Answer(CanSlave);
            case "shutterstatus":
                if (!Connected) { return NotConnected(); }
                if (!CanSetShutter) { return Answer(0, 0x400, "Not implemented"); }
                if (_shutterTarget >= 0 && !ShutterIgnoresOrders && --_shutterReads <= 0)
                {
                    ShutterStatus = _shutterTarget;
                    _shutterTarget = -1;
                }

                return Answer(ShutterStatus);
            case "azimuth":
                if (!Connected) { return NotConnected(); }
                return CanSetAzimuth ? Answer(Azimuth) : Answer(0, 0x400, "Not implemented");
            case "altitude": return Connected ? Answer(45.0) : NotConnected();
            case "athome": return Needs(AtHome) is null ? NotConnected() : Answer(AtHome);
            case "atpark": return Needs(AtPark) is null ? NotConnected() : Answer(AtPark);
            case "slaved": return Answer(false);
            case "slewing":
                if (!Connected) { return NotConnected(); }
                if (Slewing && --_moveReads <= 0)
                {
                    Slewing = false;
                    if (_moveKind == "slew") { Azimuth = _slewTarget; }
                    if (_moveKind == "park" && !MovesDoNotArrive) { AtPark = true; }
                    if (_moveKind == "findhome" && !MovesDoNotArrive) { AtHome = true; }
                    _moveKind = null;
                }

                return Answer(Slewing);
            default: return Answer(0, 0x400, $"{method} is not implemented");
        }
    }

    private string Put(string method, Dictionary<string, string> form)
    {
        if (Errors.TryGetValue(method, out var error))
        {
            return Answer(null, error, $"{method} fails with {error}");
        }

        switch (method)
        {
            case "connected":
                Connected = form.TryGetValue("Connected", out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
                return Answer(null);
            case "openshutter":
                _shutterTarget = 0;
                _shutterReads = ShutterMoveReads;
                ShutterStatus = ShutterMoveReads > 0 && !ShutterIgnoresOrders ? 2 : ShutterStatus;
                if (ShutterMoveReads == 0 && !ShutterIgnoresOrders) { ShutterStatus = 0; _shutterTarget = -1; }
                return Answer(null);
            case "closeshutter":
                _shutterTarget = 1;
                _shutterReads = ShutterMoveReads;
                ShutterStatus = ShutterMoveReads > 0 && !ShutterIgnoresOrders ? 3 : ShutterStatus;
                if (ShutterMoveReads == 0 && !ShutterIgnoresOrders) { ShutterStatus = 1; _shutterTarget = -1; }
                return Answer(null);
            case "slewtoazimuth":
                _slewTarget = double.Parse(form["Azimuth"], System.Globalization.CultureInfo.InvariantCulture);
                _moveKind = "slew";
                StartMove();
                return Answer(null);
            case "park":
                _moveKind = "park";
                StartMove();
                return Answer(null);
            case "findhome":
                _moveKind = "findhome";
                StartMove();
                return Answer(null);
            case "abortslew":
                Slewing = false;
                _moveKind = null;
                _shutterTarget = -1;
                if (ShutterStatus is 2 or 3) { ShutterStatus = 0; }
                return Answer(null);
            default:
                return Answer(null, 0x400, $"{method} is not implemented");
        }
    }

    private void StartMove()
    {
        Slewing = true;
        _moveReads = MoveReads;
        AtPark = false;
        AtHome = false;
    }
}

/// <summary>Keeps the events that a device publishes.</summary>
internal sealed class RecordingEvents : IEventPublisher
{
    private readonly object _gate = new();
    private readonly List<ISideraEvent> _events = [];

    public List<ISideraEvent> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public Task PublishAsync<TEvent>(TEvent sideraEvent, CancellationToken cancellationToken = default) where TEvent : ISideraEvent
    {
        lock (_gate)
        {
            _events.Add(sideraEvent);
        }

        return Task.CompletedTask;
    }
}
