using System.Net;
using Tansr.Sdk.Client;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Client;

public sealed class ClientOwnershipTests
{
    private sealed class Handler : HttpMessageHandler
    {
        internal int Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private static TansrClientOptions Options() => new() { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("token") };

    [Fact]
    public void InjectedClientRemainsCallerOwnedByDefault()
    {
        var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        var client = new TansrClient(Options(), http); client.Dispose();
        Assert.Equal(0, handler.Disposals);
    }

    [Fact]
    public void ExplicitOwnershipDisposesInjectedHandlerExactlyOnce()
    {
        var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        var client = new TansrClient(Options(), http, disposeInjectedClient: true); client.Dispose(); client.Dispose();
        Assert.Equal(1, handler.Disposals);
    }

    [Fact]
    public void FailedConstructionDoesNotTakeOwnershipFromCaller()
    {
        var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        var options = Options(); options.BaseUri = new Uri("http://external.test/");
        Assert.Throws<ArgumentException>(() => new TansrClient(options, http, disposeInjectedClient: true));
        Assert.Equal(0, handler.Disposals);
    }
}
