using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class OriginalNodeMemoryPublicationTests(ITestOutputHelper output)
{
    private const int FirstBytes = 1025;
    private const int ChunkBytes = 12288;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string Canonical(JsonElement value) => WireJson.CanonicalString(value, 1048576);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalNodeAndWindowsExchangeStagingCasAndImmutableReceipts(bool createByNode)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Body.Length > 32768);
        // A chunk is raw bytes and may stop inside a Unicode scalar; UTF-8 is checked at publication.
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(fixture.Body, 0, FirstBytes));
        var firstChunk = fixture.Chunk("winner", fixture.Body, 0, FirstBytes);
        var staged = await Execute(fixture, createByNode, StorageOpenMode.Create,
        [
            new(fixture.Request("head")),
            new(fixture.Begin("winner", fixture.Body, null)),
            new(firstChunk),
            new(fixture.Begin("loser", fixture.Loser, null)),
            new(fixture.Chunk("loser", fixture.Loser, 0, fixture.Loser.Length))
        ]);
        Assert.Equal(JsonValueKind.Null, staged[0].GetProperty("publication").ValueKind);
        Transfer(staged[2], "winner", "staging", FirstBytes, null);

        var finishing = new List<Operation> { new(fixture.Request("query", "winner")), new(firstChunk) };
        finishing.AddRange(fixture.Chunks("winner", fixture.Body, FirstBytes));
        var winnerCommitIndex = finishing.Count;
        finishing.Add(new(fixture.Request("commit", "winner")));
        finishing.Add(new(fixture.Request("commit", "loser")));
        finishing.Add(new(fixture.Request("commit", "never-begun")));
        var completed = await Execute(fixture, !createByNode, StorageOpenMode.Reopen, finishing);
        Transfer(completed[0], "winner", "staging", FirstBytes, null);
        Assert.Equal(Canonical(staged[2]), Canonical(completed[1]));
        var originalReceipt = completed[winnerCommitIndex];
        Transfer(originalReceipt, "winner", "committed", fixture.Body.Length, fixture.Digest);
        Transfer(completed[winnerCommitIndex + 1], "loser", "conflict", fixture.Loser.Length, null);
        Transfer(completed[winnerCommitIndex + 2], "never-begun", "unknown", null, null);

        var reading = new List<Operation>
        {
            new(fixture.Request("query", "winner")), new(fixture.Request("query", "loser")),
            new(fixture.Request("commit", "winner")), new(fixture.Request("head"))
        };
        reading.AddRange(fixture.Reads(fixture.Body, fixture.Digest));
        var replacementIndex = reading.Count;
        reading.Add(new(fixture.Begin("replacement", fixture.Replacement, fixture.Digest)));
        reading.AddRange(fixture.Chunks("replacement", fixture.Replacement));
        var restored = await Execute(fixture, createByNode, StorageOpenMode.Reopen, reading);
        Transfer(restored[0], "winner", "committed", fixture.Body.Length, fixture.Digest);
        Transfer(restored[1], "loser", "conflict", fixture.Loser.Length, null);
        Assert.Equal(Canonical(originalReceipt), Canonical(restored[2]));
        AssertHead(restored[3], fixture.Body);
        AssertBody(restored.Skip(4).Take(replacementIndex - 4), fixture.Body, fixture.Digest);

        var replaced = await Execute(fixture, !createByNode, StorageOpenMode.Reopen,
        [
            new(fixture.Request("commit", "replacement")), new(fixture.Request("query", "winner")),
            new(fixture.Request("commit", "winner")), new(fixture.Read(fixture.Digest, 0, 1), "revision_conflict"),
            new(fixture.Request("query", "winner"), "request_conflict", fixture.OtherOwner)
        ]);
        Transfer(replaced[0], "replacement", "committed", fixture.Replacement.Length, WireJson.Sha256(fixture.Replacement));
        Transfer(replaced[1], "winner", "committed", fixture.Body.Length, fixture.Digest);
        Assert.Equal(Canonical(originalReceipt), Canonical(replaced[2]));

        var final = new List<Operation> { new(fixture.Request("head")), new(fixture.Request("commit", "loser")), new(fixture.Request("query", "never-begun")) };
        final.AddRange(fixture.Reads(fixture.Replacement, WireJson.Sha256(fixture.Replacement)));
        var last = await Execute(fixture, createByNode, StorageOpenMode.Reopen, final);
        AssertHead(last[0], fixture.Replacement);
        Transfer(last[1], "loser", "conflict", fixture.Loser.Length, null);
        Transfer(last[2], "never-begun", "unknown", null, null);
        AssertBody(last.Skip(3), fixture.Replacement, WireJson.Sha256(fixture.Replacement));
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        var capacity = await reopened.GetCapacityAsync();
        Assert.Equal(3, capacity.StoredTransfers); Assert.Equal(0, capacity.StagingBytes);
        Assert.Equal(5, capacity.RemainingTransfers);
    }

    private async Task<JsonElement[]> Execute(Fixture fixture, bool node, StorageOpenMode mode, IEnumerable<Operation> operations)
    {
        var selected = operations.ToArray(); JsonElement[] responses;
        if (node)
        {
            var result = await Node(fixture, mode, selected);
            var evidence = result.GetProperty("provenance");
            output.WriteLine("Original Node memory publication: {0}", evidence.GetRawText());
            Assert.Equal("42224634aa06d83f6bd8179935a072cc406bde5a", evidence.GetProperty("sourceCommit").GetString());
            Assert.True(evidence.GetProperty("sourceCommitted").GetBoolean());
            Assert.Equal(14, evidence.GetProperty("sourceFiles").GetInt32());
            fixture.AssertMetadata(result.GetProperty("metadata").GetString()!);
            responses = result.GetProperty("responses").EnumerateArray().Select(item => item.Clone()).ToArray();
        }
        else
        {
            var values = new List<JsonElement>();
            using (var store = await SqliteMemoryPublicationStore.OpenAsync(fixture.Options(mode)))
            {
                Assert.True(store.AtomicDurablePublication); Assert.Equal(Canonical(fixture.Identity), Canonical(store.Identity));
                foreach (var operation in selected)
                {
                    if (operation.ExpectedError is { } expected)
                    {
                        var error = await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => store.ExecuteAsync(operation.Request, operation.Owner ?? fixture.Owner));
                        Assert.Equal(expected, error.Code); values.Add(Json(new { error = error.Code }));
                    }
                    else values.Add(await store.ExecuteAsync(operation.Request, operation.Owner ?? fixture.Owner));
                }
            }
            responses = values.ToArray();
        }
        Assert.Equal(selected.Length, responses.Length);
        fixture.AssertOriginalLayout();
        return responses;
    }

    private static void Transfer(JsonElement response, string id, string status, int? received, string? etag)
    {
        var transfer = response.GetProperty("transfer");
        Assert.Equal(id, transfer.GetProperty("transferId").GetString()); Assert.Equal(status, transfer.GetProperty("status").GetString());
        Assert.Equal(received, transfer.GetProperty("receivedBytes").ValueKind == JsonValueKind.Null ? (int?)null : transfer.GetProperty("receivedBytes").GetInt32());
        Assert.Equal(etag, transfer.GetProperty("etag").GetString());
    }

    private static void AssertHead(JsonElement response, byte[] body)
    {
        var head = response.GetProperty("publication"); var digest = WireJson.Sha256(body);
        Assert.Equal(body.Length, head.GetProperty("byteLength").GetInt32());
        Assert.Equal(digest, head.GetProperty("etag").GetString()); Assert.Equal(digest, head.GetProperty("sha256").GetString());
    }

    private static void AssertBody(IEnumerable<JsonElement> responses, byte[] expected, string etag)
    {
        using var body = new MemoryStream();
        foreach (var response in responses)
        {
            Assert.Equal("read", response.GetProperty("action").GetString()); Assert.Equal(etag, response.GetProperty("etag").GetString());
            Assert.Equal(body.Length, response.GetProperty("offset").GetInt64());
            var bytes = WireJson.DecodeBase64(response.GetProperty("base64").GetString()!);
            Assert.Equal(bytes.Length, response.GetProperty("byteLength").GetInt32());
            Assert.Equal(WireJson.Sha256(bytes), response.GetProperty("payloadDigest").GetString());
            body.Write(bytes); Assert.Equal(body.Length, response.GetProperty("nextOffset").GetInt64());
            Assert.Equal(body.Length == expected.Length, response.GetProperty("complete").GetBoolean());
        }
        Assert.Equal(expected, body.ToArray());
    }

    private static async Task<JsonElement> Node(Fixture fixture, StorageOpenMode mode, Operation[] operations)
    {
        string cliRoot = Environment.GetEnvironmentVariable("TANSR_TEST_RECOVERY_CLI_ROOT") ??
            throw new InvalidOperationException("TANSR_TEST_RECOVERY_CLI_ROOT is required for original Node memory publication interoperability; use scripts/test-windows.ps1.");
        cliRoot = Path.GetFullPath(cliRoot);
        var options = fixture.Options(mode);
        string payload = JsonSerializer.Serialize(new
        {
            cliRoot,
            path = fixture.Path,
            mode = mode == StorageOpenMode.Create ? "create" : "reopen",
            identity = fixture.Identity,
            scope = fixture.Scope,
            owner = fixture.Owner,
            maxTransfers = options.MaxTransfers,
            maxStagingBytes = options.MaxStagingBytes,
            maxPages = options.MaxPages,
            operations = operations.Select(operation => new { request = operation.Request, expectedError = operation.ExpectedError, owner = operation.Owner })
        });
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = cliRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false, true),
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true),
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--disable-warning=ExperimentalWarning"); process.StartInfo.ArgumentList.Add("--import");
        process.StartInfo.ArgumentList.Add(new Uri(Path.Combine(cliRoot, "node_modules", "tsx", "dist", "loader.mjs")).AbsoluteUri);
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Storage", "node-memory-publication-interop.mjs"));
        Assert.True(process.Start()); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(payload); process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return WireJson.Parse(Encoding.UTF8.GetBytes(await stdout), 2 * 1048576);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private sealed class Operation(JsonElement request, string? expectedError = null, string? owner = null)
    {
        public JsonElement Request { get; } = request;
        public string? ExpectedError { get; } = expectedError;
        public string? Owner { get; } = owner;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-net-memory-interop-" + Guid.NewGuid().ToString("N"));
        private string? metadata;
        public Fixture() { Directory.CreateDirectory(root); }
        public string Path => System.IO.Path.Combine(root, "publication.sqlite");
        public JsonElement Scope => Json(new { applicationScopeId = "net-memory-app", endUserId = "net-memory-user", authorizationRevision = "7" });
        public JsonElement Identity => Json(new
        {
            scope = new { applicationScopeId = "net-memory-app", endUserId = "net-memory-user" },
            sourceId = "source/记忆",
            sourceGeneration = "1",
            domainKey = "trusted/中文"
        });
        public string Owner => OwnerFor("original-session");
        public string OtherOwner => OwnerFor("foreign-session");
        private string OwnerFor(string sessionId) => Canonical(Json(new
        {
            scope = Scope,
            sessionId,
            binding = new
            {
                bindingId = "memory-binding",
                revision = "3",
                target = new
                {
                    executorId = "pc",
                    connectionId = "connection",
                    connectionRevision = "2",
                    workspaceId = "work",
                    workspaceRevision = "1"
                }
            }
        }));
        public byte[] Body { get; } = Encoding.UTF8.GetBytes("记忆：" + string.Concat(Enumerable.Repeat("汉字😀e\u0301", 6000)));
        public byte[] Loser { get; } = Encoding.UTF8.GetBytes("losing CAS 记忆");
        public byte[] Replacement { get; } = Encoding.UTF8.GetBytes("replacement publication 中文 👩🏽‍💻");
        public string Digest => WireJson.Sha256(Body);
        public SqliteMemoryPublicationOptions Options(StorageOpenMode mode) => new()
        {
            EnablePreview = true,
            Path = Path,
            Mode = mode,
            Identity = Identity,
            ReadContext = () => Scope,
            MaxTransfers = 8,
            MaxStagingBytes = 8388608,
            MaxPages = 8192
        };
        public JsonElement Request(string action, string? transferId = null) => Build(new Dictionary<string, object?> { ["action"] = action }, transferId);
        private JsonElement Build(Dictionary<string, object?> fields, string? transferId = null)
        {
            fields["contract"] = "terminal-services-v1"; fields["sourceId"] = "source/记忆"; fields["sourceGeneration"] = "1"; fields["domainKey"] = "trusted/中文";
            if (transferId != null) fields["transferId"] = transferId; return Json(fields);
        }
        public JsonElement Begin(string id, byte[] body, string? expected) => Build(new Dictionary<string, object?>
        { ["action"] = "begin", ["expectedEtag"] = expected, ["byteLength"] = body.Length, ["sha256"] = WireJson.Sha256(body) }, id);
        public JsonElement Chunk(string id, byte[] body, int offset, int count)
        {
            var bytes = body.AsSpan(offset, count).ToArray();
            return Build(new Dictionary<string, object?>
            {
                ["action"] = "chunk",
                ["offset"] = offset,
                ["byteLength"] = count,
                ["base64"] = Convert.ToBase64String(bytes),
                ["payloadDigest"] = WireJson.Sha256(bytes)
            }, id);
        }
        public IEnumerable<Operation> Chunks(string id, byte[] body, int from = 0)
        {
            for (int offset = from; offset < body.Length; offset += ChunkBytes) yield return new(Chunk(id, body, offset, Math.Min(ChunkBytes, body.Length - offset)));
        }
        public JsonElement Read(string etag, int offset, int length) => Build(new Dictionary<string, object?>
        { ["action"] = "read", ["etag"] = etag, ["offset"] = offset, ["length"] = length });
        public IEnumerable<Operation> Reads(byte[] body, string etag)
        {
            for (int offset = 0; offset < body.Length; offset += ChunkBytes) yield return new(Read(etag, offset, Math.Min(ChunkBytes, body.Length - offset)));
            yield return new(Read(etag, body.Length, 1));
        }
        public void AssertMetadata(string value)
        {
            metadata ??= value; Assert.Equal(metadata, value);
            var parsed = WireJson.Parse(Encoding.UTF8.GetBytes(value), 1048576);
            Assert.Equal(value, Canonical(parsed)); Assert.Equal("terminal-memory-publication-sqlite-v1", parsed.GetProperty("format").GetString());
            Assert.Equal(Canonical(Identity), Canonical(parsed.GetProperty("identity")));
        }
        public void AssertOriginalLayout()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' ORDER BY sql";
            var actual = new List<string>(); using (var rows = command.ExecuteReader()) while (rows.Read()) actual.Add(rows.GetString(0));
            using var document = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Storage", "Fixtures", "memory-publication-source-manifest.json")));
            var expected = document.RootElement.GetProperty("ddl").EnumerateArray().Select(value => value.GetString()!).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
            command.CommandText = "SELECT json FROM metadata WHERE id=1"; AssertMetadata((string)command.ExecuteScalar()!);
            command.CommandText = "PRAGMA quick_check"; Assert.Equal("ok", command.ExecuteScalar());
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
