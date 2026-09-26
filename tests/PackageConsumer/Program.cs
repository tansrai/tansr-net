using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempRoot, "tansr-net-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("https://example.invalid"),
                TokenProvider = _ => Task.FromResult("synthetic-no-request"),
                PrincipalProvider = () => "synthetic-package-principal",
                ExecutionScopeProvider = () => WireJson.Parse(Encoding.UTF8.GetBytes("{\"applicationScopeId\":\"app\",\"endUserId\":\"user\",\"authorizationRevision\":\"1\"}")),
            }))
            {
                var preview = new TerminalSessionControl(client, enablePreview: true);
                var change = WireJson.Parse(Encoding.UTF8.GetBytes("{\"thinking\":{\"budget\":2048}}"));
                var operation = preview.CreateConfigurationOperation("session", "request", 0, change);
                if (operation.Attempted || operation.Request.GetProperty("session").GetProperty("sessionContract").GetString() != "sdk1") throw new Exception("preview_contract");
                var restored = preview.RestoreConfigurationOperation(operation.Request, operation.Scope);
                if (!restored.Attempted || WireJson.CanonicalString(restored.Request) != WireJson.CanonicalString(operation.Request)) throw new Exception("preview_restore");
            }
            var control = WireJson.Parse(Encoding.UTF8.GetBytes("{\"sequence\":\"9223372036854775807\"}"));
            if (WireJson.CanonicalString(control) != "{\"sequence\":\"9223372036854775807\"}") throw new Exception("wire");
            await ConsumeMemoryPublication(root);
            using (var workspace = new WindowsWorkspace(root))
            {
                var backend = new WindowsExecutorBackend("package-consumer", new[] { new WindowsExecutorWorkspace("workspace", "1", workspace) });
                WireJson.ValidateNamed("ExecutorRegistrationRequest", backend.Registration);
                workspace.WriteAtomic("hello.txt", Encoding.UTF8.GetBytes("原生 SDK 消费 😀"));
                if (Encoding.UTF8.GetString(workspace.Read("hello.txt")) != "原生 SDK 消费 😀") throw new Exception("workspace");
                var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var request = new WindowsProcessRequest(command, new[] { "/d", "/s", "/c", "echo tansr-package-consumer" }, () => workspace.AcquireProcessDirectory());
                var process = await new WindowsProcessExecutor().ExecuteAsync(request, null, CancellationToken.None);
                if (!process.CleanupConfirmed || process.ExitCode != 0 || !process.StandardOutput.Contains("tansr-package-consumer")) throw new Exception("process");
            }
            Console.WriteLine("PACKAGE_CONSUMER_OK " + Environment.Version);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("PACKAGE_CONSUMER_FAILED " + error.GetType().FullName + " " + error.Message);
            return 1;
        }
        finally
        {
            if (!root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("cleanup_scope");
            Directory.Delete(root, true);
        }
    }

    private static async Task ConsumeMemoryPublication(string root)
    {
        var scope = WireJson.Parse(Encoding.UTF8.GetBytes("{\"applicationScopeId\":\"package-app\",\"endUserId\":\"package-user\",\"authorizationRevision\":\"1\"}"));
        var identity = WireJson.Parse(Encoding.UTF8.GetBytes("{\"scope\":{\"applicationScopeId\":\"package-app\",\"endUserId\":\"package-user\"},\"sourceId\":\"package-source\",\"sourceGeneration\":\"1\",\"domainKey\":\"package-domain\"}"));
        var owner = WireJson.CanonicalString(WireJson.Parse(Encoding.UTF8.GetBytes("{\"scope\":" + scope.GetRawText() + ",\"sessionId\":\"package-session\",\"binding\":{\"bindingId\":\"package-binding\",\"revision\":\"1\",\"target\":{\"executorId\":\"package-executor\",\"connectionId\":\"package-connection\",\"connectionRevision\":\"1\",\"workspaceId\":\"package-workspace\",\"workspaceRevision\":\"1\"}}}")));
        var options = new SqliteMemoryPublicationOptions
        {
            EnablePreview = true, Path = Path.Combine(root, "memory.sqlite"), Mode = StorageOpenMode.Create,
            Identity = identity, ReadContext = () => scope, MaxTransfers = 2
        };
        var body = Encoding.UTF8.GetBytes("包消费记忆 😀");
        var digest = WireJson.Sha256(body);
        var commit = MemoryRequest("commit", writer => writer.WriteString("transferId", "package-transfer"));
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            var host = new WindowsMemoryPublicationHost(store, enablePreview: true);
            var tool = host.CreateTool();
            if (!store.AtomicDurablePublication || WireJson.CanonicalString(host.Identity) != WireJson.CanonicalString(identity) ||
                tool.Name != "TansrTerminalMemoryPublication" || tool.DefinitionDigest.Length != 64) throw new Exception("memory_host");
            var head = await store.ExecuteAsync(MemoryRequest("head"), owner);
            if (head.GetProperty("publication").ValueKind != JsonValueKind.Null) throw new Exception("memory_empty");
            await store.ExecuteAsync(MemoryRequest("begin", writer =>
            {
                writer.WriteString("transferId", "package-transfer"); writer.WriteNull("expectedEtag");
                writer.WriteNumber("byteLength", body.Length); writer.WriteString("sha256", digest);
            }), owner);
            await store.ExecuteAsync(MemoryRequest("chunk", writer =>
            {
                writer.WriteString("transferId", "package-transfer"); writer.WriteNumber("offset", 0); writer.WriteNumber("byteLength", body.Length);
                writer.WriteString("base64", Convert.ToBase64String(body)); writer.WriteString("payloadDigest", digest);
            }), owner);
            var receipt = await store.ExecuteAsync(commit, owner);
            if (receipt.GetProperty("transfer").GetProperty("status").GetString() != "committed") throw new Exception("memory_commit");
            await store.CloseAsync();
        }
        options.Mode = StorageOpenMode.Reopen;
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            var receipt = await store.ExecuteAsync(commit, owner);
            if (receipt.GetProperty("transfer").GetProperty("etag").GetString() != digest) throw new Exception("memory_reopen");
            var read = await store.ExecuteAsync(MemoryRequest("read", writer =>
            { writer.WriteString("etag", digest); writer.WriteNumber("offset", 0); writer.WriteNumber("length", body.Length); }), owner);
            if (Encoding.UTF8.GetString(WireJson.DecodeBase64(read.GetProperty("base64").GetString()!)) != Encoding.UTF8.GetString(body) ||
                !read.GetProperty("complete").GetBoolean()) throw new Exception("memory_read");
        }
    }

    private static JsonElement MemoryRequest(string action, Action<Utf8JsonWriter>? fields = null)
    {
        using (var memory = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(memory))
            {
                writer.WriteStartObject(); writer.WriteString("contract", "terminal-services-v1"); writer.WriteString("action", action);
                writer.WriteString("sourceId", "package-source"); writer.WriteString("sourceGeneration", "1"); writer.WriteString("domainKey", "package-domain");
                if (fields != null) fields(writer); writer.WriteEndObject();
            }
            return WireJson.Parse(memory.ToArray());
        }
    }
}
