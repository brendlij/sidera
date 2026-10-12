using System.Net;
using System.Text;

namespace Sidera.Alpaca.Tests;

/// <summary>The address of a device, and the form of the Alpaca conversation: what is sent, what is read from the answer, and what is not followed.</summary>
public sealed class AlpacaProtocolTests
{
    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("192.168.1.20:11111", "192.168.1.20", 11111, 0)]
    [InlineData("192.168.1.20:11111/2", "192.168.1.20", 11111, 2)]
    [InlineData("http://roof.local:32323/0", "roof.local", 32323, 0)]
    [InlineData("  localhost:5555  ", "localhost", 5555, 0)]
    [InlineData("[::1]:32323/1", "::1", 32323, 1)]
    [InlineData("roof.local", "roof.local", 80, 0)]
    public void AnAddressIsRead_WithOrWithoutTheNumberOfTheDevice(string text, string host, int port, int number)
    {
        Assert.True(AlpacaEndpoint.TryParse(text, out var endpoint, out var problem));

        Assert.Null(problem);
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(number, endpoint.DeviceNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.20:11111/x")]
    [InlineData("192.168.1.20:11111/-1")]
    [InlineData("192.168.1.20:99999")]
    [InlineData("https://192.168.1.20:11111")]
    [InlineData("a b:11111")]
    public void AnAddressThatIsNotOne_IsRefusedWithAnExplanation(string text)
    {
        Assert.False(AlpacaEndpoint.TryParse(text, out var endpoint, out var problem));

        Assert.Null(endpoint);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void AnAddress_IsWrittenTheWayItIsRead()
    {
        Assert.True(AlpacaEndpoint.TryParse("[::1]:32323/1", out var endpoint, out _));

        Assert.Equal("[::1]:32323/1", endpoint!.ToString());
        Assert.Equal("http://[::1]:32323/", endpoint.ServerUri.ToString());
    }

    [Fact]
    public async Task EveryRequest_CarriesTheClientAndTheTransaction_AndAPutCarriesItsParameters()
    {
        var handler = new Scripted(_ => Json("""{"Value":true,"ErrorNumber":0,"ErrorMessage":""}"""));
        using var client = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 11111, 3), "Dome", new HttpClient(handler));

        await client.GetBoolAsync("connected", default);
        await client.PutAsync("slewtoazimuth", default, ("Azimuth", "123.5"));

        Assert.Equal("/api/v1/dome/3/connected", handler.Seen[0].RequestUri!.AbsolutePath);
        Assert.Contains("ClientID=", handler.Seen[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("ClientTransactionID=1", handler.Seen[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Put, handler.Seen[1].Method);
        Assert.Contains("Azimuth=123.5", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("ClientTransactionID=2", handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorOfTheDevice_BecomesAnExceptionWithItsNumberAndMessage()
    {
        var handler = new Scripted(_ => Json("""{"Value":0,"ErrorNumber":1031,"ErrorMessage":"Dome simulator is not connected"}"""));
        using var client = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 11111), "dome", new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<AlpacaException>(() => client.GetDoubleAsync("azimuth", default));

        Assert.Equal(1031, failure.ErrorNumber);
        Assert.True(failure.IsNotConnected);
        Assert.Equal("Dome simulator is not connected", failure.Message);
    }

    [Fact]
    public async Task AFeatureTheDriverDoesNotHave_IsNull_NotAnError()
    {
        var handler = new Scripted(_ => Json("""{"ErrorNumber":1024,"ErrorMessage":"Not implemented"}"""));
        using var client = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 11111), "dome", new HttpClient(handler));

        Assert.Null(await client.TryGetBoolAsync("canslave", default));
        Assert.Null(await client.TryGetDoubleAsync("altitude", default));
    }

    [Fact]
    public async Task AnAnswerThatIsNotAlpaca_IsReportedAsSuch()
    {
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>router login</html>") });
        using var client = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 80), "dome", new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<AlpacaCommunicationException>(() => client.GetBoolAsync("connected", default));

        Assert.Contains("did not answer like an Alpaca server", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirect_IsNotFollowed()
    {
        var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://192.0.2.99/elsewhere") } });
        using var client = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 11111), "dome", new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<AlpacaCommunicationException>(() => client.GetBoolAsync("connected", default));

        Assert.Contains("redirect", failure.Message, StringComparison.Ordinal);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerInTime_IsReportedAsSlow_AndACallerWhoCancelsJustCancels()
    {
        var handler = new Scripted(_ => throw new InvalidOperationException("not reached"));
        var slow = new SlowHandler();
        using var client = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 11111), "dome", new HttpClient(slow), TimeSpan.FromMilliseconds(50));

        var failure = await Assert.ThrowsAsync<AlpacaCommunicationException>(() => client.GetBoolAsync("connected", default));
        Assert.Contains("did not answer within", failure.Message, StringComparison.Ordinal);

        using var cancel = new CancellationTokenSource(20);
        using var patient = new AlpacaClient(new AlpacaEndpoint("192.0.2.1", 11111), "dome", new HttpClient(slow), TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patient.GetBoolAsync("connected", cancel.Token));
        Assert.Empty(handler.Seen);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
