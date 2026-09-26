using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class NativeSnapshotHostTests
{
    [Fact]
    public async Task ReopeningAnExistingMirrorWithoutItsKeyDoesNotCreateANewKeyOrModifyTheMirror()
    {
        var root = Path.Combine(Path.GetTempPath(), "tansr-snapshot-key-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var scope = Path.Combine(root, "scope.json"); var config = Path.Combine(root, "configuration.json"); var mirrors = Path.Combine(root, "mirrors");
            await File.WriteAllTextAsync(scope, "{\"scope\":{\"applicationScopeId\":\"app\",\"endUserId\":\"user\"}}");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { scopeFile = scope, directory = mirrors }));
            using var http = new HttpClient(new Handler()); using var client = new TansrClient(new()
            { BaseUri = new("https://serve.test"), TokenProvider = _ => Task.FromResult("synthetic") }, http);
            var session = await client.CreateSessionAsync(new());
            var type = Assembly.Load("ConsoleAssistant").GetType("Tansr.Examples.NativeSnapshotHost", true)!;
            async Task<IDisposable> Open()
            {
                var task = (Task)type.GetMethod("OpenAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [session, new Uri("https://serve.test"), config, CancellationToken.None])!;
                await task; return (IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            Task<string> Enable(object host) => (Task<string>)type.GetMethod("EnableAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [CancellationToken.None])!;
            using (var original = await Open()) await Enable(original);
            var database = Directory.GetFiles(mirrors, "*.sqlite").Single(); var key = Directory.GetFiles(mirrors, "*.key").Single();
            var before = await File.ReadAllBytesAsync(database); File.Delete(key);
            using var reopened = await Open();
            Assert.Equal("snapshot_key_missing_existing_mirror_preserved", (await Assert.ThrowsAsync<IOException>(() => Enable(reopened))).Message);
            Assert.False(File.Exists(key)); Assert.Equal(before, await File.ReadAllBytesAsync(database));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
            if (path == "/v2/sessions") return Task.FromResult(Json("{\"sessionId\":\"s\",\"lastSeq\":0,\"resumed\":false}"));
            if (path == "/v2/sessions/s") return Task.FromResult(Json("{\"sessionId\":\"s\",\"lastSeq\":0}"));
            if (path.EndsWith("/checkpoints", StringComparison.Ordinal)) return Task.FromResult(Json("{\"sessionId\":\"s\",\"checkpointId\":\"own-checkpoint\"}"));
            if (request.Method == HttpMethod.Delete && path.EndsWith("/checkpoints/own-checkpoint", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (path.EndsWith("/export", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("complete original snapshot")) { Headers = { ContentType = new("application/octet-stream") } } });
            throw new InvalidOperationException("unexpected_snapshot_key_fixture_route");
        }
    }
}
