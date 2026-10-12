using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Sidera.Alpaca;

/// <summary>The Alpaca server answered, and said that the request failed (the <c>ErrorNumber</c> and <c>ErrorMessage</c> of every Alpaca response).</summary>
public sealed class AlpacaException(int errorNumber, string message) : Exception(message)
{
    /// <summary>The ASCOM error number: 0x400 not implemented, 0x401 invalid value, 0x407 not connected, 0x40B invalid operation; 0x500 and up are the driver's own.</summary>
    public int ErrorNumber { get; } = errorNumber;

    public bool IsNotImplemented => ErrorNumber is 0x400 or 0x40C;

    public bool IsNotConnected => ErrorNumber == 0x407;
}

/// <summary>The server could not be reached, or did not answer in time: the request was not (known to be) carried out.</summary>
public sealed class AlpacaCommunicationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One Alpaca REST conversation with one server: GET and PUT of an API path, the form of Alpaca (<c>ClientID</c> and <c>ClientTransactionID</c> on every request, <c>Value</c>, <c>ErrorNumber</c> and
/// <c>ErrorMessage</c> in every answer). It sends nothing but to the <see cref="BaseUri"/> it was made for, and does not follow a redirect to another address.
/// </summary>
internal sealed class AlpacaTransport : IDisposable
{
    private static int _clientIds = Random.Shared.Next(1, 65000);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _timeout;
    private readonly int _clientId = Interlocked.Increment(ref _clientIds);
    private uint _transaction;

    public AlpacaTransport(Uri baseUri, HttpClient? http = null, TimeSpan? timeout = null)
    {
        BaseUri = baseUri;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    public Uri BaseUri { get; }

    /// <summary>GET <paramref name="path"/> (relative to <see cref="BaseUri"/>); the <c>Value</c> of the answer.</summary>
    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path + "?" + Common(), null, cancellationToken);

    /// <summary>PUT <paramref name="path"/> with a form of parameters; the <c>Value</c> of the answer (most PUTs have none).</summary>
    public Task<JsonElement> PutAsync(string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>>(form ?? []);
        fields.AddRange(CommonFields());
        return SendAsync(HttpMethod.Put, path, new FormUrlEncodedContent(fields), cancellationToken);
    }

    private IEnumerable<KeyValuePair<string, string>> CommonFields() =>
    [
        new("ClientID", _clientId.ToString(CultureInfo.InvariantCulture)),
        new("ClientTransactionID", Interlocked.Increment(ref _transaction).ToString(CultureInfo.InvariantCulture)),
    ];

    private string Common() => string.Join('&', CommonFields().Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value)));

    private async Task<JsonElement> SendAsync(HttpMethod method, string pathAndQuery, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseUri, pathAndQuery)) { Content = content };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        string body;
        HttpStatusCode status;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if ((int)status is >= 300 and < 400)
            {
                throw new AlpacaCommunicationException($"The Alpaca server at {BaseUri.Authority} answered with a redirect; Sidera does not follow it.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AlpacaCommunicationException($"The Alpaca server at {BaseUri.Authority} did not answer within {_timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new AlpacaCommunicationException($"The Alpaca server at {BaseUri.Authority} could not be reached: {ex.Message}", ex);
        }

        return Read(status, body);
    }

    private JsonElement Read(HttpStatusCode status, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new AlpacaCommunicationException($"The server at {BaseUri.Authority} did not answer like an Alpaca server (HTTP {(int)status}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AlpacaCommunicationException($"The server at {BaseUri.Authority} did not answer like an Alpaca server (HTTP {(int)status}).");
            }

            var number = 0;
            string? message = null;
            JsonElement? value = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals("ErrorNumber", StringComparison.OrdinalIgnoreCase))
                {
                    number = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt32() : 0;
                }
                else if (property.Name.Equals("ErrorMessage", StringComparison.OrdinalIgnoreCase))
                {
                    message = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (property.Name.Equals("Value", StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Clone();
                }
            }

            if (number != 0)
            {
                throw new AlpacaException(number, string.IsNullOrWhiteSpace(message) ? $"The device reported error {number}." : message);
            }

            // A list answer of the management API has a Value too; a PUT usually has none.
            return value ?? default;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

/// <summary>The API of one Alpaca device: <c>/api/v1/{type}/{number}/{method}</c>.</summary>
internal sealed class AlpacaClient : IDisposable
{
    private readonly AlpacaTransport _transport;
    private readonly string _prefix;

    public AlpacaClient(AlpacaEndpoint endpoint, string deviceType, HttpClient? http = null, TimeSpan? timeout = null)
    {
        Endpoint = endpoint;
        _transport = new AlpacaTransport(endpoint.ServerUri, http, timeout);
        _prefix = string.Create(CultureInfo.InvariantCulture, $"api/v1/{deviceType.ToLowerInvariant()}/{endpoint.DeviceNumber}/");
    }

    public AlpacaEndpoint Endpoint { get; }

    public Task<bool> GetBoolAsync(string method, CancellationToken cancellationToken) => GetAsync(method, v => v.GetBoolean(), cancellationToken);

    public Task<int> GetIntAsync(string method, CancellationToken cancellationToken) => GetAsync(method, v => v.GetInt32(), cancellationToken);

    public Task<double> GetDoubleAsync(string method, CancellationToken cancellationToken) => GetAsync(method, v => v.GetDouble(), cancellationToken);

    public Task<string> GetStringAsync(string method, CancellationToken cancellationToken) =>
        GetAsync(method, v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty, cancellationToken);

    private async Task<T> GetAsync<T>(string method, Func<JsonElement, T> read, CancellationToken cancellationToken)
    {
        var value = await _transport.GetAsync(_prefix + method, cancellationToken).ConfigureAwait(false);
        try
        {
            return read(value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new AlpacaCommunicationException($"The device at {Endpoint} answered '{method}' in a form that Sidera cannot read.", ex);
        }
    }

    /// <summary>A boolean property of the device, or <c>null</c> when the driver does not implement it (the capability is then not there).</summary>
    public async Task<bool?> TryGetBoolAsync(string method, CancellationToken cancellationToken)
    {
        try
        {
            return await GetBoolAsync(method, cancellationToken).ConfigureAwait(false);
        }
        catch (AlpacaException ex) when (ex.IsNotImplemented)
        {
            return null;
        }
    }

    public async Task<double?> TryGetDoubleAsync(string method, CancellationToken cancellationToken)
    {
        try
        {
            return await GetDoubleAsync(method, cancellationToken).ConfigureAwait(false);
        }
        catch (AlpacaException ex) when (ex.IsNotImplemented)
        {
            return null;
        }
    }

    public async Task<int?> TryGetIntAsync(string method, CancellationToken cancellationToken)
    {
        try
        {
            return await GetIntAsync(method, cancellationToken).ConfigureAwait(false);
        }
        catch (AlpacaException ex) when (ex.IsNotImplemented)
        {
            return null;
        }
    }

    public Task PutAsync(string method, CancellationToken cancellationToken, params (string Name, string Value)[] parameters) =>
        _transport.PutAsync(_prefix + method, parameters.Select(p => new KeyValuePair<string, string>(p.Name, p.Value)), cancellationToken);

    public void Dispose() => _transport.Dispose();
}
