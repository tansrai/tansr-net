using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Examples;

public sealed class TurnInputEditorTests
{
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<(string Method, string Path, string? Body)> Requests = [];
        internal Func<string, HttpResponseMessage> Input = AcceptedInput;
        internal string Capabilities = "{\"version\":1,\"text\":true,\"textBlocks\":true,\"image\":false,\"memoryAck\":true,\"durableAck\":false,\"receiptRetention\":\"current-and-last-turn\",\"target\":{\"historyEpoch\":\"epoch\",\"turnId\":\"turn\"}}";
        internal HttpResponseMessage? Status;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method.Method, path, body));
            if (path == "/api/sessions") return Json("{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":-1}");
            if (path.EndsWith("/input-capabilities", StringComparison.Ordinal)) return Json(Capabilities);
            if (request.Method == HttpMethod.Get)
            {
                if (Status is not null) return Status;
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]), StringComparer.Ordinal);
                return Receipt(Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split('/').Last()), query["historyEpoch"], query["turnId"], "consumed", HttpStatusCode.OK);
            }
            return Input(body!);
        }
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage AcceptedInput(string body)
    {
        using var document = JsonDocument.Parse(body); var input = document.RootElement; var target = input.GetProperty("target");
        return Receipt(input.GetProperty("inputId").GetString()!, target.GetProperty("historyEpoch").GetString()!, target.GetProperty("turnId").GetString()!, "accepted", HttpStatusCode.Accepted);
    }
    private static HttpResponseMessage Receipt(string inputId, string historyEpoch, string turnId, string state, HttpStatusCode status) => Json(JsonSerializer.Serialize(new
    {
        outcome = "accepted",
        receipt = new { inputId, sessionId = "s", historyEpoch, turnId, durability = "memory", state, ordinal = 0, revision = 0, source = "strict" }
    }), status);
    private static TansrClientOptions Options() => new() { BaseUri = new Uri("https://example.invalid"), TokenProvider = _ => Task.FromResult("synthetic") };

    [Fact]
    public async Task UnknownRetainsFullOriginalBeforePostAndCannotBecomeANewTurn()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new CreateSessionOptions());
        TurnInputRecord? saved = null; var text = string.Concat(Enumerable.Repeat("完整长文🙂\n", 9000));
        handler.Input = body => { Assert.NotNull(saved); Assert.Equal(text, saved.Text); throw new HttpRequestException("synthetic drop"); };
        var editor = new TurnInputEditor(session, null, record => saved = record);
        var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => editor.InsertAsync(text));
        Assert.Equal("network_error", failure.Code);
        Assert.Equal("unconfirmed", saved!.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.InsertAsync("cannot silently start new id"));
        var original = Assert.Single(handler.Requests, x => x.Method == "POST" && x.Path.EndsWith("/inputs", StringComparison.Ordinal));
        using (var json = JsonDocument.Parse(original.Body!)) Assert.Equal(text, json.RootElement.GetProperty("content").GetProperty("text").GetString());
        handler.Input = body => { Assert.Equal(original.Body, body); return AcceptedInput(body); };
        var reopened = new TurnInputEditor(session, saved, record => saved = record);
        await reopened.RetryOriginalAsync();
        Assert.Equal("accepted", saved.Outcome);
        Assert.Equal(2, handler.Requests.Count(x => x.Path.EndsWith("/inputs", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, x => x.Path.Contains("messages", StringComparison.Ordinal) || x.Path.Contains("interrupt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistenceFailurePreventsAnyInputPost()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var editor = new TurnInputEditor(await client.CreateSessionAsync(new CreateSessionOptions()), null, _ => throw new IOException("disk full"));
        await Assert.ThrowsAsync<IOException>(() => editor.InsertAsync("完整正文"));
        Assert.DoesNotContain(handler.Requests, x => x.Path.EndsWith("/inputs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingOriginalReceiptKeepsUnknownAndOriginalTarget()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        handler.Status = Json("{\"outcome\":\"rejected\",\"code\":\"input_not_found\"}", HttpStatusCode.NotFound);
        var saved = new TurnInputRecord("s", "original", "old-epoch", "old-turn", "original body", "unconfirmed", null);
        var editor = new TurnInputEditor(await client.CreateSessionAsync(new CreateSessionOptions()), saved, record => saved = record);
        var status = await editor.QueryAsync();
        Assert.Contains("不能推断未执行", status); Assert.Equal("not_found", saved.Outcome);
        Assert.Contains(handler.Requests, x => x.Path == "/api/sessions/s/inputs/original?historyEpoch=old-epoch&turnId=old-turn");
        Assert.DoesNotContain(handler.Requests, x => x.Path.Contains("capabilities", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.InsertAsync("new"));
    }

    [Fact]
    public async Task InactiveTurnIsAnExplicitFailureWithoutSendingOrRebuilding()
    {
        using var handler = new Handler { Capabilities = "{\"text\":true,\"memoryAck\":true,\"target\":null}" };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var editor = new TurnInputEditor(await client.CreateSessionAsync(new CreateSessionOptions()), null, _ => throw new InvalidOperationException("should not persist"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => editor.InsertAsync("same turn"));
        Assert.Equal("no_active_turn_for_insertion", error.Message);
        Assert.Single(handler.Requests, x => x.Method == "POST");
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("unconfirmed")]
    [InlineData("not_found")]
    public async Task RestoredUnknownClosedRetryDoesNotEraseOriginalUncertainty(string previousOutcome)
    {
        using var handler = new Handler { Input = _ => Json("{\"outcome\":\"closed\",\"code\":\"epoch_mismatch\"}", HttpStatusCode.Conflict) };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var saved = new TurnInputRecord("s", "original", "old-epoch", "old-turn", "original full body", previousOutcome, null);
        var editor = new TurnInputEditor(await client.CreateSessionAsync(new CreateSessionOptions()), saved, record => saved = record);
        await Assert.ThrowsAsync<TansrHttpException>(() => editor.RetryOriginalAsync());
        Assert.Equal("unconfirmed", saved.Outcome); Assert.False(saved.CanStartAnother);
        Assert.Equal("original", saved.InputId); Assert.Equal("old-epoch", saved.HistoryEpoch); Assert.Equal("old-turn", saved.TurnId); Assert.Equal("original full body", saved.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.InsertAsync("must not replace the original"));
        Assert.Single(handler.Requests, x => x.Path.EndsWith("/inputs", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, x => x.Path.Contains("capabilities", StringComparison.Ordinal) || x.Path.Contains("messages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryOnlyAcceptsReceiptForTheSavedIdentityAndOriginalTarget()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var saved = new TurnInputRecord("s", "original", "old-epoch", "old-turn", "original body", "unconfirmed", null);
        var editor = new TurnInputEditor(await client.CreateSessionAsync(new CreateSessionOptions()), saved, record => saved = record);
        await editor.QueryAsync();
        Assert.Equal("accepted", saved.Outcome); Assert.Contains("consumed", saved.ReceiptJson);
        Assert.Single(handler.Requests, x => x.Path == "/api/sessions/s/inputs/original?historyEpoch=old-epoch&turnId=old-turn");
        Assert.DoesNotContain(handler.Requests, x => x.Path.Contains("capabilities", StringComparison.Ordinal));
    }
}
