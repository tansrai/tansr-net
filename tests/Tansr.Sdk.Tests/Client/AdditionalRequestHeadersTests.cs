using System.Net;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Tests.Client;

public sealed class AdditionalRequestHeadersTests
{
    [Fact]
    public async Task TrustedAdditionalCredentialIsCopiedAndDoesNotReplaceBearerOrPrincipal()
    {
        var headers = new Dictionary<string, string> { ["x-tansr-demo-user-token"] = "original-user-ticket" };
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var options = Options(); options.PrincipalProvider = () => "opaque-app/user"; options.AdditionalRequestHeaders = headers;
        using var client = new TansrClient(options, http);
        headers["x-tansr-demo-user-token"] = "another-user-ticket";
        options.AdditionalRequestHeaders = null;
        await client.CreateSessionAsync(new());
        Assert.Equal("original-user-ticket", handler.Headers["x-tansr-demo-user-token"]);
        Assert.Equal("Bearer original-controller-ticket", handler.Headers["Authorization"]);
        Assert.DoesNotContain("x-tansr-end-user", handler.Headers.Keys);
        Assert.DoesNotContain("opaque-app/user", handler.Headers.Values);
    }

    [Fact]
    public async Task DefaultDoesNotIntroduceASecondAuthenticationProtocol()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        await client.CreateSessionAsync(new());
        Assert.DoesNotContain(handler.Headers.Keys, name => name.StartsWith("x-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Authorization", "ticket")]
    [InlineData("Cookie", "ticket")]
    [InlineData("Host", "example.invalid")]
    [InlineData("x-forwarded-for", "127.0.0.1")]
    [InlineData("X-Real-IP", "127.0.0.1")]
    [InlineData("x-bad\r\n", "ticket")]
    [InlineData("x-user", "ticket\r\nX-Second: injected")]
    [InlineData("x-user", "秘密")]
    [InlineData("x-user", "")]
    public void UnsafeHeadersFailBeforeAnyNetworkAndDoNotExposeCredential(string name, string value)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var options = Options(); options.AdditionalRequestHeaders = new Dictionary<string, string> { [name] = value };
        var error = Assert.Throws<ArgumentException>(() => new TansrClient(options, http));
        Assert.Equal("Invalid additional request headers. (Parameter 'AdditionalRequestHeaders')", error.Message);
        Assert.Empty(handler.Headers);
    }

    [Fact]
    public void NamesAreCaseInsensitiveAndTotalMemoryIsBounded()
    {
        Assert.Throws<ArgumentException>(() => RequestHeaderSnapshot.Copy(new Dictionary<string, string> { ["x-user"] = "first", ["X-User"] = "second" }));
        Assert.Throws<ArgumentException>(() => RequestHeaderSnapshot.Copy(Enumerable.Range(0, 17).ToDictionary(i => "x-" + i, _ => "value")));
        Assert.Throws<ArgumentException>(() => RequestHeaderSnapshot.Copy(new Dictionary<string, string> { ["x-user"] = new('a', 4097) }));
        Assert.Throws<ArgumentException>(() => RequestHeaderSnapshot.Copy(Enumerable.Range(0, 4).ToDictionary(i => "x-" + i, _ => new string('a', 4096))));
    }

    private static TansrClientOptions Options() => new() { BaseUri = new Uri("https://serve.test"), TokenProvider = _ => Task.FromResult("original-controller-ticket") };
    private sealed class Handler : HttpMessageHandler
    {
        internal Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"sessionId\":\"session\",\"resumed\":false,\"lastSeq\":-1}", Encoding.UTF8, "application/json") });
        }
    }
}
