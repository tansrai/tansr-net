using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>设备记忆 publication SQLite 介质，支持原 Node 明文格式和显式本地加密格式。只搬运不透明 UTF-8 正文；记忆决策与删除语义由 Serve 负责。</summary>
public sealed partial class SqliteMemoryPublicationStore : IMemoryPublicationStore, IEncryptedMemoryPublicationStore, IDisposable
{
    public const string Format = "terminal-memory-publication-sqlite-v1";
    public const string EncryptedFormat = MemoryPublicationBodyCipher.Format;
    public const int MaximumBodyBytes = 4194304;
    public const int MaximumChunkBytes = 12288;
    public const int MaximumTransfers = 1048576;
    public const int MaximumStagingBytes = 33554432;
    public const int MaximumPages = 262144;
    private const int MaximumMetadataBytes = 1048576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Schema =
    {
        "CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL) STRICT",
        "CREATE TABLE publication (id INTEGER PRIMARY KEY CHECK(id=1), etag TEXT NOT NULL, body BLOB NOT NULL) STRICT",
        "CREATE TABLE transfers (id TEXT PRIMARY KEY, owner TEXT NOT NULL, request TEXT NOT NULL, status TEXT NOT NULL, received INTEGER NOT NULL, body BLOB, etag TEXT) STRICT",
    };
    private const string EncryptionSchema = "CREATE TABLE encryption (id INTEGER PRIMARY KEY CHECK(id=1), key_check BLOB NOT NULL) STRICT";
    private readonly MemoryPublicationBodyCipher? _cipher;
    private static readonly object OwnersGate = new();
    private static readonly Dictionary<string, OpenOwner> Owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly StorageFileIdentity _parent, _file;
    private readonly SqliteMemoryPublicationOptions _options;
    private readonly OpenOwner _owner;
    private readonly string _metadata;
    private bool _busy, _poisoned, _uncertain, _committed, _closed, _dbClosed, _fileClosed, _parentClosed;

    private SqliteMemoryPublicationStore(SqliteConnection connection, StorageFileIdentity parent, StorageFileIdentity file,
        SqliteMemoryPublicationOptions options, OpenOwner owner, string metadata, MemoryPublicationBodyCipher? cipher)
    { _connection = connection; _parent = parent; _file = file; _options = options; _owner = owner; _metadata = metadata; _cipher = cipher; }

    public bool AtomicDurablePublication => true;
    /// <summary>正文加密能力；路径、身份、请求及回执元数据保持可见且通过 AAD 认证。</summary>
    public bool EncryptedBody => _cipher != null;
    public JsonElement Identity => _options.Identity.Clone();

    public static Task<SqliteMemoryPublicationStore> OpenAsync(SqliteMemoryPublicationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options == null || !options.EnablePreview || options.ReadContext == null ||
            options.Mode != StorageOpenMode.Create && options.Mode != StorageOpenMode.Reopen ||
            options.MaxTransfers <= 0 || options.MaxTransfers > MaximumTransfers ||
            options.MaxStagingBytes < MaximumBodyBytes || options.MaxStagingBytes > MaximumStagingBytes ||
            options.MaxPages < 8 || options.MaxPages > MaximumPages) throw new SqliteMemoryPublicationException("invalid_request");
        string path = StorageFileIdentity.FullPath(options.Path);
        var fixedOptions = new SqliteMemoryPublicationOptions
        {
            EnablePreview = true,
            Path = path,
            Mode = options.Mode,
            Identity = ValidateIdentity(options.Identity),
            MaxTransfers = options.MaxTransfers,
            MaxStagingBytes = options.MaxStagingBytes,
            MaxPages = options.MaxPages,
            ReadContext = options.ReadContext,
            KeyProvider = options.KeyProvider,
            AuthorizeRecovery = options.AuthorizeRecovery,
        };
        string? keyId = fixedOptions.KeyProvider == null ? null : MemoryPublicationBodyCipher.ReadKeyId(fixedOptions.KeyProvider);
        var owner = new OpenOwner { Signature = Signature(fixedOptions, keyId) }; OpenOwner? maintenance = null;
        lock (OwnersGate)
        {
            if (Owners.TryGetValue(path, out var prior))
            {
                if (prior.Maintaining) { prior.Poisoned = true; throw new StorageException("reentrant"); }
                if (prior.Opening) prior.Poisoned = true;
                if (prior.Cleanup == null || prior.Signature != owner.Signature) throw new StorageException("storage_error");
                prior.Maintaining = true; prior.Poisoned = false; maintenance = prior;
            }
            else Owners.Add(path, owner);
        }
        if (maintenance != null)
        {
            try
            {
                var original = Scope(fixedOptions.ReadContext, fixedOptions.Identity);
                void CheckMaintenance()
                {
                    Require(!maintenance.Poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested();
                    var current = Scope(fixedOptions.ReadContext, fixedOptions.Identity); Require(!maintenance.Poisoned, "reentrant");
                    Require(Equal(original, current), "context_changed"); cancellationToken.ThrowIfCancellationRequested();
                }
                CheckMaintenance(); maintenance.Cleanup!(CheckMaintenance); CheckMaintenance(); Release(path, maintenance);
                throw new StorageException("reconciliation_required");
            }
            finally { lock (OwnersGate) maintenance.Maintaining = false; }
        }
        StorageFileIdentity? parent = null, file = null; SqliteConnection? connection = null; SqliteMemoryPublicationStore? store = null;
        try
        {
            var scope = Scope(fixedOptions.ReadContext, fixedOptions.Identity);
            void CheckOpening()
            {
                Require(!owner.Poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested();
                Require(Equal(scope, Scope(fixedOptions.ReadContext, fixedOptions.Identity)), "context_changed");
                Require(!owner.Poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested(); parent?.Check(); file?.Check();
            }
            CheckOpening(); parent = StorageFileIdentity.Open(Path.GetDirectoryName(path)!, true); CheckOpening();
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix))
            {
                Require(fixedOptions.Mode != StorageOpenMode.Create, "identity_mismatch");
                using var sidecar = StorageFileIdentity.Open(path + suffix, false);
            }
            file = StorageFileIdentity.Open(path, false, fixedOptions.Mode == StorageOpenMode.Create); CheckOpening();
            string metadata = Metadata(fixedOptions, parent, file, keyId);
            var cipher = fixedOptions.KeyProvider == null ? null : new MemoryPublicationBodyCipher(fixedOptions.KeyProvider, keyId!, metadata);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString()); connection.Open();
            store = new SqliteMemoryPublicationStore(connection, parent, file, fixedOptions, owner, metadata, cipher);
            if (fixedOptions.Mode == StorageOpenMode.Reopen)
            {
                Require((string?)store.Scalar("SELECT json FROM metadata WHERE id=1 AND length(CAST(json AS BLOB))<=1048576") == metadata, "identity_mismatch");
                Require(store.Number("PRAGMA page_size") == 4096 && (string?)store.Scalar("PRAGMA quick_check") == "ok");
            }
            CheckOpening(); store.Exec("PRAGMA busy_timeout=0; PRAGMA locking_mode=EXCLUSIVE; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL");
            if (fixedOptions.Mode == StorageOpenMode.Create) store.Exec("PRAGMA page_size=4096; PRAGMA journal_mode=WAL");
            else Require((string?)store.Scalar("PRAGMA journal_mode") == "wal");
            Require(store.Number("PRAGMA max_page_count=" + fixedOptions.MaxPages.ToString(CultureInfo.InvariantCulture)) == fixedOptions.MaxPages, "capacity_exceeded");
            store.Transaction(() =>
            {
                if (fixedOptions.Mode == StorageOpenMode.Create)
                {
                    foreach (string sql in Schema) store.Exec(sql);
                    store.Exec("INSERT INTO metadata VALUES(1,$json)", ("$json", metadata));
                    if (cipher != null) { store.Exec(EncryptionSchema); store.Exec("INSERT INTO encryption VALUES(1,$check)", ("$check", cipher.Seal("key-check", Array.Empty<byte>()))); }
                }
                store.CheckFixed(); store.Audit();
            }, CheckOpening);
            CheckOpening(); owner.Opening = false; return Task.FromResult(store);
        }
        catch (Exception error)
        {
            bool committed = store?._committed == true;
            bool dbClosed = connection == null, fileClosed = file == null, parentClosed = parent == null;
            void Cleanup(Action check)
            {
                check();
                if (parent != null) { using var current = StorageFileIdentity.Open(Path.GetDirectoryName(path)!, true, metadataOnly: true); Require(current.Device == parent.Device && current.Inode == parent.Inode, "identity_mismatch"); }
                if (file != null) { using var current = StorageFileIdentity.Open(path, false, metadataOnly: true); Require(current.Device == file.Device && current.Inode == file.Inode, "identity_mismatch"); }
                if (!dbClosed) { connection!.Dispose(); dbClosed = true; check(); }
                if (!fileClosed) { file!.Dispose(); fileClosed = true; check(); }
                if (!parentClosed) { parent!.Dispose(); parentClosed = true; check(); }
            }
            try { Cleanup(() => { }); Release(path, owner); }
            catch { lock (OwnersGate) { owner.Opening = false; owner.Cleanup = Cleanup; } throw new StorageException("reconciliation_required"); }
            if (committed) throw new StorageException("reconciliation_required");
            if (error is SqliteException sqlite) throw new StorageException(sqlite.SqliteErrorCode == 13 ? "capacity_exceeded" : "storage_error");
            if (error is IOException || error is UnauthorizedAccessException) throw new StorageException("storage_error");
            throw;
        }
    }

    public Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default)
    {
        JsonElement fixedRequest = default, currentOwner = default;
        return Task.FromResult(Run(check =>
        {
            string action = String(fixedRequest, "action");
            if (action == "head")
            {
                var row = Publication();
                return Response(fixedRequest, writer =>
                {
                    writer.WritePropertyName("publication");
                    if (row == null) writer.WriteNullValue();
                    else { writer.WriteStartObject(); writer.WriteString("etag", row.Etag); writer.WriteNumber("byteLength", row.Body.Length); writer.WriteString("sha256", WireJson.Sha256(row.Body)); writer.WriteEndObject(); }
                });
            }
            if (action == "read")
            {
                var row = Publication(); Need(row != null && row.Etag == String(fixedRequest, "etag"), "revision_conflict");
                int offset = fixedRequest.GetProperty("offset").GetInt32(), length = fixedRequest.GetProperty("length").GetInt32(); Need(offset <= row!.Body.Length, "invalid_request");
                var bytes = row.Body.Skip(offset).Take(length).ToArray();
                return Response(fixedRequest, writer =>
                {
                    writer.WriteString("etag", row.Etag); writer.WriteNumber("offset", offset); writer.WriteString("base64", Convert.ToBase64String(bytes));
                    writer.WriteNumber("byteLength", bytes.Length); writer.WriteString("payloadDigest", WireJson.Sha256(bytes));
                    writer.WriteNumber("nextOffset", offset + bytes.Length); writer.WriteBoolean("complete", offset + bytes.Length == row.Body.Length);
                });
            }
            string transferId = String(fixedRequest, "transferId"); var stage = Stage(transferId);
            if (stage != null && stage.Owner != ownerCanonical)
            {
                Need(action == "query" && _options.AuthorizeRecovery != null, "request_conflict");
                bool allowed = _options.AuthorizeRecovery!(new SqliteMemoryPublicationRecoveryContext(_options.Identity, transferId, Owner(stage.Owner), currentOwner));
                check(); Need(allowed, "request_conflict");
            }
            if (action == "query") return TransferResponse(fixedRequest, stage);
            if (action == "begin")
            {
                if (stage != null) Need(Equal(stage.Request, fixedRequest), "request_conflict");
                else Transaction(() =>
                {
                    var capacity = Capacity(); int length = fixedRequest.GetProperty("byteLength").GetInt32();
                    Need(capacity.StoredTransfers < _options.MaxTransfers && capacity.StagingBytes + length <= _options.MaxStagingBytes, "capacity_exceeded");
                    if (_cipher == null)
                        Exec("INSERT INTO transfers VALUES($id,$owner,$request,'staging',0,zeroblob($length),NULL)", ("$id", transferId), ("$owner", ownerCanonical), ("$request", Text(fixedRequest)), ("$length", length));
                    else Exec("INSERT INTO transfers VALUES($id,$owner,$request,'staging',0,$body,NULL)", ("$id", transferId), ("$owner", ownerCanonical), ("$request", Text(fixedRequest)),
                        ("$body", EncodeTransfer(ownerCanonical, fixedRequest, "staging", 0, new byte[length], null)));
                }, check);
            }
            else
            {
                if (stage == null) return TransferResponse(fixedRequest, null);
                if (action == "chunk")
                {
                    byte[] bytes = Convert.FromBase64String(String(fixedRequest, "base64")); int offset = fixedRequest.GetProperty("offset").GetInt32();
                    Need(bytes.Length == fixedRequest.GetProperty("byteLength").GetInt32() && bytes.Length <= MaximumChunkBytes && Convert.ToBase64String(bytes) == String(fixedRequest, "base64") && WireJson.Sha256(bytes) == String(fixedRequest, "payloadDigest"));
                    Need(stage.Status == "staging" && stage.Body != null && offset + bytes.Length <= stage.Request.GetProperty("byteLength").GetInt32(), "request_conflict");
                    if (offset < stage.Received) Need(offset + bytes.Length <= stage.Received && bytes.SequenceEqual(stage.Body!.Skip(offset).Take(bytes.Length)), "request_conflict");
                    else
                    {
                        Need(offset == stage.Received, "request_conflict"); byte[] updated = (byte[])stage.Body!.Clone(); Buffer.BlockCopy(bytes, 0, updated, offset, bytes.Length);
                        Transaction(() => Exec("UPDATE transfers SET body=$body,received=$received WHERE id=$id", ("$body", EncodeTransfer(stage.Owner, stage.Request, "staging", offset + bytes.Length, updated, null)), ("$received", offset + bytes.Length), ("$id", transferId)), check);
                    }
                }
                else if (stage.Status == "staging")
                {
                    Need(stage.Body != null && stage.Received == stage.Request.GetProperty("byteLength").GetInt32() && WireJson.Sha256(stage.Body) == String(stage.Request, "sha256"));
                    try { StrictUtf8.GetString(stage.Body!); } catch (DecoderFallbackException) { throw new SqliteMemoryPublicationException("integrity_mismatch"); }
                    string? expected = stage.Request.GetProperty("expectedEtag").GetString(); string sha256 = String(stage.Request, "sha256");
                    Transaction(() =>
                    {
                        if (Publication()?.Etag != expected) Exec("UPDATE transfers SET status='conflict',body=$body WHERE id=$id", ("$body", EncodeTransfer(stage.Owner, stage.Request, "conflict", stage.Received, null, null)), ("$id", transferId));
                        else
                        {
                            Exec("INSERT INTO publication VALUES(1,$etag,$body) ON CONFLICT(id) DO UPDATE SET etag=excluded.etag,body=excluded.body", ("$etag", sha256), ("$body", EncodePublication(sha256, stage.Body!)));
                            Exec("UPDATE transfers SET status='committed',body=$body,etag=$etag WHERE id=$id", ("$body", EncodeTransfer(stage.Owner, stage.Request, "committed", stage.Received, null, sha256)), ("$etag", sha256), ("$id", transferId));
                        }
                    }, check);
                }
            }
            return TransferResponse(fixedRequest, Stage(transferId));
        }, cancellationToken, () =>
        {
            fixedRequest = Copy(request, "MemoryPublicationRequest");
            Need(ownerCanonical != null && Encoding.UTF8.GetByteCount(ownerCanonical) <= 8192, "invalid_request"); currentOwner = Owner(ownerCanonical!);
            Need(SameSubject(currentOwner.GetProperty("scope"), _options.Identity.GetProperty("scope")), "request_conflict");
            Need(new[] { "sourceId", "sourceGeneration", "domainKey" }.All(name => String(fixedRequest, name) == String(_options.Identity, name)), "stale_generation");
        }));
    }

    public Task<SqliteMemoryPublicationCapacity> GetCapacityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run(_ => Capacity(), cancellationToken));

    /// <summary>读取本机已提交的不透明出版快照；仍核对当前主体及文件身份。不生成执行 owner，不运行记忆治理，也不证明远端授权仍有效。</summary>
    public Task<byte[]?> ReadPublicationAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Run(_ => Publication()?.Body.ToArray(), cancellationToken));

    public Task CloseAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            if (_closed) return; _busy = true;
            try
            {
                if (!_dbClosed) { _connection.Dispose(); _dbClosed = true; }
                if (!_fileClosed) { _file.Dispose(); _fileClosed = true; }
                if (!_parentClosed) { _parent.Dispose(); _parentClosed = true; }
                _closed = true; Release(_options.Path, _owner);
            }
            finally { _busy = false; }
        }
    }

    private T Run<T>(Func<Action, T> work, CancellationToken cancellationToken, Action? prepare = null, Action? verifyResult = null)
    {
        lock (_gate)
        {
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            Require(!_closed && !_dbClosed, "closed"); Require(!_uncertain, "reconciliation_required"); cancellationToken.ThrowIfCancellationRequested();
            _busy = true; _poisoned = false; _committed = false;
            try
            {
                prepare?.Invoke(); var scope = Scope(_options.ReadContext, _options.Identity);
                void Check()
                {
                    Require(!_poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested();
                    var current = Scope(_options.ReadContext, _options.Identity); Require(!_poisoned, "reentrant");
                    Require(Equal(scope, current), "context_changed"); CheckFixed();
                    if (_cipher != null)
                    {
                        // Key providers are host callbacks too. Recheck authority after the final key read.
                        Require(!_poisoned, "reentrant");
                        current = Scope(_options.ReadContext, _options.Identity); Require(!_poisoned, "reentrant");
                        Require(Equal(scope, current), "context_changed"); _parent.Check(); _file.Check();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Check(); var result = work(Check); Check(); verifyResult?.Invoke(); return result;
            }
            catch (Exception error)
            {
                if (_committed) { _uncertain = true; throw new StorageException("reconciliation_required"); }
                if (error is SqliteException sqlite) throw new StorageException(sqlite.SqliteErrorCode == 13 ? "capacity_exceeded" : "storage_error");
                throw;
            }
            finally { _busy = false; }
        }
    }

    private void Transaction(Action work, Action check)
    {
        bool begun = false, committing = false;
        try { check(); Exec("BEGIN IMMEDIATE"); begun = true; work(); check(); committing = true; Exec("COMMIT"); begun = false; _committed = true; }
        catch
        {
            bool unknown = committing;
            if (begun) { try { if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle!) == 0) Exec("ROLLBACK"); } catch { unknown = true; } }
            if (unknown) { _uncertain = true; throw new StorageException("reconciliation_required"); }
            throw;
        }
    }

    private void CheckFixed()
    {
        _parent.Check(); _file.Check();
        var schema = _cipher == null ? Schema : Schema.Concat(new[] { EncryptionSchema }).ToArray();
        Need(Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_'") == schema.Length);
        Need(Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' AND (length(CAST(sql AS BLOB))>4096 OR length(CAST(name AS BLOB))>128)") == 0);
        using (var command = Command("SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' LIMIT 4")) using (var reader = command.ExecuteReader())
        { var found = new HashSet<string>(StringComparer.Ordinal); while (reader.Read()) { Need(!reader.IsDBNull(0) && schema.Contains(reader.GetString(0))); found.Add(reader.GetString(0)); } Need(found.Count == schema.Length); }
        Need(Number("SELECT count(*) FROM metadata") == 1 && (string?)Scalar("SELECT json FROM metadata WHERE id=1 AND length(CAST(json AS BLOB))<=1048576") == _metadata);
        if (_cipher != null)
        {
            Need(Number("SELECT count(*) FROM encryption") == 1);
            var keyCheck = Scalar("SELECT key_check FROM encryption WHERE id=1 AND length(key_check)=28") as byte[];
            Need(keyCheck != null); _cipher.Open("key-check", keyCheck!);
        }
    }

    private void Audit()
    {
        Publication(); Capacity();
        Need(Number("SELECT count(*) FROM transfers WHERE length(CAST(id AS BLOB))>512") == 0);
        using var command = Command("SELECT id FROM transfers"); using var reader = command.ExecuteReader(); while (reader.Read()) Stage(reader.GetString(0));
    }

    private PublicationRow? Publication()
    {
        Need(Number("SELECT count(*) FROM publication") <= 1 && Number("SELECT count(*) FROM publication WHERE id<>1 OR length(body)>$maximum OR length(etag)>256", ("$maximum", MaximumEncodedBodyBytes)) == 0);
        using var command = Command("SELECT etag,body FROM publication WHERE id=1"); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string etag = reader.GetString(0); byte[] body = (byte[])reader.GetValue(1);
        if (_cipher != null) body = _cipher.Open(PublicationContext(etag), body);
        Need(WireJson.Sha256(body) == etag); return new PublicationRow(etag, body);
    }

    private TransferRow? Stage(string id)
    {
        Need(Number("SELECT count(*) FROM transfers WHERE id=$id AND (length(request)>4096 OR length(owner)>8192 OR length(body)>$maximum OR length(etag)>256)", ("$id", id), ("$maximum", MaximumEncodedBodyBytes)) == 0);
        using var command = Command("SELECT owner,request,status,received,body,etag FROM transfers WHERE id=$id", ("$id", id)); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string owner = reader.GetString(0); var ownerValue = Owner(owner); Need(SameSubject(ownerValue.GetProperty("scope"), _options.Identity.GetProperty("scope")));
        var request = Copy(WireJson.Parse(Encoding.UTF8.GetBytes(reader.GetString(1)), 32768), "MemoryPublicationRequest");
        Need(String(request, "action") == "begin" && String(request, "transferId") == id && new[] { "sourceId", "sourceGeneration", "domainKey" }.All(name => String(request, name) == String(_options.Identity, name)));
        string status = reader.GetString(2); long received = reader.GetInt64(3); byte[]? body = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4); string? etag = reader.IsDBNull(5) ? null : reader.GetString(5);
        Need(new[] { "staging", "committed", "conflict" }.Contains(status) && received >= 0 && received <= request.GetProperty("byteLength").GetInt32());
        if (_cipher != null)
        {
            Need(body != null); body = _cipher.Open(TransferContext(owner, request, status, received, etag), body!);
            if (status != "staging") { Need(body.Length == 0); body = null; }
        }
        Need(status == "staging" ? body != null && body.Length == request.GetProperty("byteLength").GetInt32() && etag == null : body == null && (status == "committed" ? received == request.GetProperty("byteLength").GetInt32() && etag == String(request, "sha256") : etag == null));
        return new TransferRow(owner, request, status, checked((int)received), body, etag);
    }

    private SqliteMemoryPublicationCapacity Capacity()
    {
        long count = Number("SELECT count(*) FROM transfers"), bytes = Number("SELECT COALESCE(SUM(length(body)-$overhead),0) FROM transfers WHERE status='staging'", ("$overhead", _cipher == null ? 0 : MemoryPublicationBodyCipher.Overhead));
        Need(count <= _options.MaxTransfers && bytes >= 0 && bytes <= _options.MaxStagingBytes);
        return new SqliteMemoryPublicationCapacity(_options.MaxTransfers, count, _options.MaxStagingBytes, bytes, _options.MaxPages, Number("PRAGMA page_count"), Number("PRAGMA freelist_count"));
    }

    private int MaximumEncodedBodyBytes => MaximumBodyBytes + (_cipher == null ? 0 : MemoryPublicationBodyCipher.Overhead);
    private static string PublicationContext(string etag) => Text(Object(writer =>
    { writer.WriteString("row", "publication"); writer.WriteString("etag", etag); }));
    private static string TransferContext(string owner, JsonElement request, string status, long received, string? etag) => Text(Object(writer =>
    {
        writer.WriteString("row", "transfer"); writer.WriteString("owner", owner); Property(writer, "request", request);
        writer.WriteString("status", status); writer.WriteNumber("received", received); writer.WriteString("etag", etag);
    }));
    private byte[] EncodePublication(string etag, byte[] body) => _cipher == null ? body : _cipher.Seal(PublicationContext(etag), body);
    private object EncodeTransfer(string owner, JsonElement request, string status, int received, byte[]? body, string? etag)
        => _cipher == null ? (object?)body ?? DBNull.Value : _cipher.Seal(TransferContext(owner, request, status, received, etag), body ?? Array.Empty<byte>());

    private static JsonElement Response(JsonElement request, Action<Utf8JsonWriter> write)
    {
        var response = Object(writer => { foreach (string name in new[] { "contract", "sourceId", "sourceGeneration", "domainKey", "action" }) Property(writer, name, request.GetProperty(name)); write(writer); });
        TerminalCandidateContract.Validate("MemoryPublicationResponse", response); return response;
    }
    private static JsonElement TransferResponse(JsonElement request, TransferRow? row) => Response(request, writer =>
    {
        writer.WriteStartObject("transfer"); writer.WriteString("transferId", String(request, "transferId")); writer.WriteString("status", row?.Status ?? "unknown");
        if (row == null) writer.WriteNull("receivedBytes"); else writer.WriteNumber("receivedBytes", row.Received);
        writer.WriteString("etag", row?.Etag); writer.WriteEndObject();
    });

    private static JsonElement ValidateIdentity(JsonElement input)
    {
        var value = WireJson.DecodeControl(WireJson.EncodeControl(input, 32768), 32768); Fields(value, "scope", "sourceId", "sourceGeneration", "domainKey"); Fields(value.GetProperty("scope"), "applicationScopeId", "endUserId");
        var scope = Object(writer => { foreach (var property in value.GetProperty("scope").EnumerateObject()) property.WriteTo(writer); writer.WriteString("authorizationRevision", "0"); }); WireJson.ValidateNamed("Scope", scope);
        Copy(Object(writer => { writer.WriteString("contract", "terminal-services-v1"); writer.WriteString("action", "head"); foreach (string name in new[] { "sourceId", "sourceGeneration", "domainKey" }) Property(writer, name, value.GetProperty(name)); }), "MemoryPublicationRequest"); return value;
    }
    private static JsonElement Owner(string text)
    {
        var value = WireJson.DecodeControl(Encoding.UTF8.GetBytes(text), 32768); Fields(value, "scope", "sessionId", "binding");
        WireJson.ValidateNamed("Scope", value.GetProperty("scope")); WireJson.ValidateNamed("LegacyId", value.GetProperty("sessionId")); WireJson.ValidateNamed("ExecutionBinding", value.GetProperty("binding"));
        Need(Text(value) == text); return value;
    }
    private static JsonElement Scope(Func<JsonElement> read, JsonElement identity)
    {
        try { var value = WireJson.DecodeControl(WireJson.EncodeControl(read(), MaximumMetadataBytes), MaximumMetadataBytes); WireJson.ValidateNamed("Scope", value); Require(SameSubject(value, identity.GetProperty("scope")), "context_changed"); return value; }
        catch { throw new StorageException("context_changed"); }
    }
    private static bool SameSubject(JsonElement a, JsonElement b) => String(a, "applicationScopeId") == String(b, "applicationScopeId") && String(a, "endUserId") == String(b, "endUserId");
    private static JsonElement Copy(JsonElement value, string definition)
    { var fixedValue = WireJson.DecodeControl(WireJson.EncodeControl(value, 32768), 32768); TerminalCandidateContract.Validate(definition, fixedValue); return fixedValue; }
    private static void Fields(JsonElement value, params string[] names) => Need(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(item => item.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(names.OrderBy(x => x, StringComparer.Ordinal)), "invalid_request");
    private static JsonElement Object(Action<Utf8JsonWriter> write)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); } return WireJson.Parse(stream.ToArray(), MaximumMetadataBytes); }
    private static void Property(Utf8JsonWriter writer, string name, JsonElement value) { writer.WritePropertyName(name); value.WriteTo(writer); }
    private static string Text(JsonElement value) => WireJson.CanonicalString(value, MaximumMetadataBytes);
    private static bool Equal(JsonElement left, JsonElement right) => Text(left) == Text(right);
    private static string String(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static void Need(bool condition, string code = "integrity_mismatch") { if (!condition) throw new SqliteMemoryPublicationException(code); }
    private static void Require(bool condition, string code = "integrity_mismatch") { if (!condition) throw new StorageException(code); }
    private static void Release(string path, OpenOwner owner) { lock (OwnersGate) if (Owners.TryGetValue(path, out var current) && ReferenceEquals(current, owner)) Owners.Remove(path); }
    private SqliteCommand Command(string sql, params (string Name, object Value)[] args) { var command = _connection.CreateCommand(); command.CommandText = sql; foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value); return command; }
    private object? Scalar(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); return command.ExecuteScalar(); }
    private long Number(string sql, params (string Name, object Value)[] args) => Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);
    private void Exec(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); command.ExecuteNonQuery(); }
    private static string Metadata(SqliteMemoryPublicationOptions options, StorageFileIdentity parent, StorageFileIdentity file, string? keyId) => Text(Object(writer =>
    {
        writer.WriteString("format", keyId == null ? Format : EncryptedFormat);
        if (keyId != null) writer.WriteString("keyId", keyId);
        Property(writer, "identity", options.Identity);
        writer.WriteStartObject("limits"); writer.WriteNumber("maxTransfers", options.MaxTransfers); writer.WriteNumber("maxStagingBytes", options.MaxStagingBytes); writer.WriteEndObject();
        writer.WriteNumber("maxPages", options.MaxPages); writer.WriteNumber("pageSize", 4096);
        writer.WriteStartObject("physical"); writer.WriteStartObject("directory"); writer.WriteString("dev", parent.Device); writer.WriteString("ino", parent.Inode); writer.WriteEndObject();
        writer.WriteStartObject("file"); writer.WriteString("dev", file.Device); writer.WriteString("ino", file.Inode); writer.WriteEndObject(); writer.WriteEndObject();
    }));
    private static string Signature(SqliteMemoryPublicationOptions options, string? keyId) => Text(Object(writer =>
    {
        writer.WriteString("format", keyId == null ? Format : EncryptedFormat);
        if (keyId != null) writer.WriteString("keyId", keyId);
        Property(writer, "identity", options.Identity);
        writer.WriteStartObject("limits"); writer.WriteNumber("maxTransfers", options.MaxTransfers); writer.WriteNumber("maxStagingBytes", options.MaxStagingBytes); writer.WriteEndObject(); writer.WriteNumber("maxPages", options.MaxPages);
    }));
    private sealed class OpenOwner
    { internal bool Opening = true; internal bool Poisoned, Maintaining; internal string Signature = ""; internal Action<Action>? Cleanup; }
    private sealed class PublicationRow
    { internal PublicationRow(string etag, byte[] body) { Etag = etag; Body = body; } internal string Etag { get; } internal byte[] Body { get; } }
    private sealed class TransferRow
    {
        internal TransferRow(string owner, JsonElement request, string status, int received, byte[]? body, string? etag) { Owner = owner; Request = request; Status = status; Received = received; Body = body; Etag = etag; }
        internal string Owner { get; }
        internal JsonElement Request { get; }
        internal string Status { get; }
        internal int Received { get; }
        internal byte[]? Body { get; }
        internal string? Etag { get; }
    }
}
