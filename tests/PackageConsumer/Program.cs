using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;

internal static class Program
{
    private static Task<int> Main(string[] args) => RunAsync(args);

    internal static async Task<int> RunAsync(string[] args)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempRoot, "tansr-net-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--require-no-node") && NodeIsOnPath()) throw new Exception("node_visible_on_path");
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
            await ConsumeMemoryPublication(root, encrypted: true);
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
            var localServe = Argument(args, "--local-serve");
            if (localServe != null) await ConsumeLocalServe(root, localServe, Argument(args, "--local-serve-sha256") ?? throw new Exception("serve_digest_required"));
            var receipt = Argument(args, "--receipt");
            if (receipt != null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                using var current = Process.GetCurrentProcess();
                var document = new
                {
                    outcome = "passed", runtime = Environment.Version.ToString(), processId = current.Id,
                    executable = current.MainModule!.FileName, is64BitProcess = Environment.Is64BitProcess,
                    elevated = principal.IsInRole(WindowsBuiltInRole.Administrator), nodeOnPath = NodeIsOnPath(),
                    coreAssembly = typeof(TansrClient).Assembly.Location,
                    windowsAssembly = typeof(WindowsWorkspace).Assembly.Location,
                    sqliteAssembly = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.Location,
                    localServe = localServe != null, cleanupConfirmed = true, encryptedMemoryPublication = true, encryptedExecutionJournal = true
                };
                File.WriteAllText(Path.GetFullPath(receipt), JsonSerializer.Serialize(document), new UTF8Encoding(false));
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

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new Exception("missing_argument_" + name);
        return args[index + 1];
    }

    private static bool NodeIsOnPath() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(path => path.Length != 0 && File.Exists(Path.Combine(path.Trim('"'), "node.exe")));

    private static async Task ConsumeLocalServe(string root, string executable, string digest)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var workspace = new WindowsWorkspace(root);
        using var host = await LocalServeHost.StartAsync(new LocalServeHostOptions(executable, digest, workspace) { Port = port });
        using var client = host.CreateClient();
        var sessions = await client.ListSessionsAsync();
        if (!host.IsReady || sessions.GetProperty("sessions").ValueKind != JsonValueKind.Array) throw new Exception("local_serve_list");
        var stopped = await host.StopAsync();
        if (!stopped.CleanupConfirmed || !stopped.IoSettled || host.IsReady) throw new Exception("local_serve_cleanup");
        try { await client.ListSessionsAsync(); throw new Exception("local_serve_survived_owner"); }
        catch (TansrProtocolException error) when (error.Code == "serve_not_ready") { }
    }

    private static async Task ConsumeMemoryPublication(string root, bool encrypted = false)
    {
        if (encrypted) { root = Path.Combine(root, "encrypted"); Directory.CreateDirectory(root); }
        var key = encrypted ? CurrentUserDpapiArchiveKeyProvider.Create(Path.Combine(root, "key.json"), "package-key") : null;
        var scope = WireJson.Parse(Encoding.UTF8.GetBytes("{\"applicationScopeId\":\"package-app\",\"endUserId\":\"package-user\",\"authorizationRevision\":\"1\"}"));
        var identity = WireJson.Parse(Encoding.UTF8.GetBytes("{\"scope\":{\"applicationScopeId\":\"package-app\",\"endUserId\":\"package-user\"},\"sourceId\":\"package-source\",\"sourceGeneration\":\"1\",\"domainKey\":\"package-domain\"}"));
        var owner = WireJson.CanonicalString(WireJson.Parse(Encoding.UTF8.GetBytes("{\"scope\":" + scope.GetRawText() + ",\"sessionId\":\"package-session\",\"binding\":{\"bindingId\":\"package-binding\",\"revision\":\"1\",\"target\":{\"executorId\":\"package-executor\",\"connectionId\":\"package-connection\",\"connectionRevision\":\"1\",\"workspaceId\":\"package-workspace\",\"workspaceRevision\":\"1\"}}}")));
        var options = new SqliteMemoryPublicationOptions
        {
            EnablePreview = true, Path = Path.Combine(root, "memory.sqlite"), Mode = StorageOpenMode.Create,
            Identity = identity, ReadContext = () => scope, MaxTransfers = 2, KeyProvider = key
        };
        var body = Encoding.UTF8.GetBytes("包消费记忆 😀");
        var digest = WireJson.Sha256(body);
        var commit = MemoryRequest("commit", writer => writer.WriteString("transferId", "package-transfer"));
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            var host = new WindowsMemoryPublicationHost(store, enablePreview: true, requireEncryption: encrypted);
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
            if (store.EncryptedBody != encrypted) throw new Exception("memory_encryption_capability");
            if (key != null) ProbeEncryptedFiles(root, body, key);
            await store.CloseAsync();
        }
        if (key != null) options.KeyProvider = CurrentUserDpapiArchiveKeyProvider.Open(Path.Combine(root, "key.json"), "package-key");
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
        if (key != null)
        {
            await ConsumeEncryptedJournal(root, scope, key, body);
            var before = File.ReadAllBytes(options.Path); options.KeyProvider = null;
            try { using var rejected = await SqliteMemoryPublicationStore.OpenAsync(options); throw new Exception("encrypted_memory_opened_without_key"); }
            catch (StorageException) { }
            if (!before.SequenceEqual(File.ReadAllBytes(options.Path))) throw new Exception("encrypted_memory_rewritten");
            ProbeEncryptedFiles(root, body, key);
        }
    }

    private static async Task ConsumeEncryptedJournal(string root, JsonElement scope, IArchiveKeyProvider key, byte[] body)
    {
        JsonElement Operation(string id)
        {
            var unsigned = WireJson.Parse(Encoding.UTF8.GetBytes("{\"protocol\":\"sdk2-ext-v1\",\"operationId\":\"" + id +
                "\",\"sessionId\":\"package-session\",\"scope\":" + scope.GetRawText() +
                ",\"binding\":{\"bindingId\":\"package-binding\",\"revision\":\"1\",\"target\":{\"executorId\":\"package-executor\",\"connectionId\":\"package-connection\",\"connectionRevision\":\"1\",\"workspaceId\":\"package-workspace\",\"workspaceRevision\":\"1\"}}," +
                "\"toolName\":\"Write\",\"request\":{\"operation\":\"fs.write\",\"args\":{\"path\":\"memory.bin\",\"expectedHash\":null,\"bytesBase64\":\"" + Convert.ToBase64String(body) +
                "\"}},\"expiresAt\":\"2099-01-01T00:00:00.000Z\"}"));
            return WireJson.Parse(Encoding.UTF8.GetBytes(unsigned.GetRawText().TrimEnd('}') + ",\"digest\":\"" +
                WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(unsigned)) + "\"}"));
        }
        var operation = Operation("package-completed"); var pending = Operation("package-pending");
        var receipt = WireJson.Parse(Encoding.UTF8.GetBytes("{\"protocol\":\"sdk2-ext-v1\",\"operationId\":\"package-completed\",\"digest\":\"" +
            operation.GetProperty("digest").GetString() + "\",\"executorId\":\"package-executor\",\"connectionId\":\"package-connection\",\"status\":\"completed\"," +
            "\"result\":{\"operation\":\"fs.write\",\"args\":{\"hash\":\"" + WireJson.Sha256(body) + "\"}},\"errorCode\":null}"));
        var options = new SqliteExecutorJournalOptions { Path = Path.Combine(root, "journal.sqlite"), Mode = StorageOpenMode.Create,
            ApplicationScopeId = "package-app", EndUserId = "package-user", ExecutorId = "package-executor", ReadContext = () => scope, KeyProvider = key };
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            if (!journal.EncryptedAtRest || (await journal.ClaimAsync(operation)).Status != ExecutorJournalClaimStatus.Claimed ||
                (await journal.ClaimAsync(pending)).Status != ExecutorJournalClaimStatus.Claimed) throw new Exception("encrypted_journal_claim");
            await journal.CompleteAsync(operation, receipt); ProbeEncryptedFiles(root, body, key);
        }
        options.Mode = StorageOpenMode.Reopen;
        options.KeyProvider = CurrentUserDpapiArchiveKeyProvider.Open(Path.Combine(root, "key.json"), "package-key");
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            var replay = await journal.ClaimAsync(operation);
            if (replay.Status != ExecutorJournalClaimStatus.Completed || WireJson.CanonicalString(replay.Receipt!.Value) != WireJson.CanonicalString(receipt) ||
                (await journal.ClaimAsync(pending)).Status != ExecutorJournalClaimStatus.Pending || await journal.ReceiptAsync(pending) != null)
                throw new Exception("encrypted_journal_original_facts");
        }
        var before = File.ReadAllBytes(options.Path); options.KeyProvider = null;
        try { using var rejected = await SqliteExecutorJournal.OpenAsync(options); throw new Exception("encrypted_journal_opened_without_key"); }
        catch (StorageException) { }
        if (!before.SequenceEqual(File.ReadAllBytes(options.Path))) throw new Exception("encrypted_journal_rewritten");
    }

    private static void ProbeEncryptedFiles(string root, byte[] body, IArchiveKeyProvider provider)
    {
        byte[] key = provider.ReadKey();
        try
        {
            foreach (var path in Directory.GetFiles(root))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream(); file.CopyTo(buffer); byte[] stored = buffer.ToArray();
                string text = Encoding.UTF8.GetString(stored);
                if (text.Contains(Encoding.UTF8.GetString(body)) || text.Contains(Convert.ToBase64String(body)) || text.Contains(Convert.ToBase64String(key)))
                    throw new Exception("encrypted_media_plaintext");
                for (int offset = 0; offset <= stored.Length - key.Length; offset++)
                {
                    int index = 0;
                    while (index < key.Length && stored[offset + index] == key[index]) index++;
                    if (index == key.Length) throw new Exception("encrypted_media_key");
                }
            }
        }
        finally { Array.Clear(key, 0, key.Length); }
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
