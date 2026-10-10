using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>Encrypted opaque objects, permanent dual-key entries and original transfer facts in one FULL SQLite transaction.</summary>
public sealed partial class SqliteTerminalPersistenceStore : ITerminalPersistenceStore, IDisposable
{
    public const string Format = "terminal-persistence-encrypted-net-sqlite-v1";
    private const int MaximumMetadataBytes = 262144;
    private const int MetadataReserve = 262144;
    private static readonly string[] Schema =
    {
        "CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL) STRICT",
        "CREATE TABLE encryption (id INTEGER PRIMARY KEY CHECK(id=1), key_check BLOB NOT NULL) STRICT",
        "CREATE TABLE state (id INTEGER PRIMARY KEY CHECK(id=1), body BLOB NOT NULL) STRICT",
        "CREATE TABLE objects (kind TEXT NOT NULL, hash TEXT NOT NULL, bytes INTEGER NOT NULL, body BLOB NOT NULL, PRIMARY KEY(kind,hash)) STRICT",
        "CREATE TABLE entries (primary_key TEXT PRIMARY KEY, secondary_key TEXT NOT NULL UNIQUE, ordinal INTEGER NOT NULL UNIQUE, body BLOB NOT NULL) STRICT",
        "CREATE TABLE transfers (id TEXT PRIMARY KEY, status TEXT NOT NULL, body BLOB NOT NULL) STRICT",
    };
    private readonly MemoryPublicationBodyCipher _cipher;
    private static readonly object OwnersGate = new();
    private static readonly Dictionary<string, OpenOwner> Owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly StorageFileIdentity _parent, _file;
    private readonly SqliteTerminalPersistenceOptions _options;
    private readonly OpenOwner _owner;
    private readonly string _metadata;
    private long? _dataVersion;
    private bool _busy, _poisoned, _uncertain, _committed, _closed, _dbClosed, _fileClosed, _parentClosed;

    private SqliteTerminalPersistenceStore(SqliteConnection connection, StorageFileIdentity parent, StorageFileIdentity file,
        SqliteTerminalPersistenceOptions options, OpenOwner owner, string metadata, MemoryPublicationBodyCipher cipher)
    { _connection = connection; _parent = parent; _file = file; _options = options; _owner = owner; _metadata = metadata; _cipher = cipher; }

    public bool AtomicDurablePersistence => true;
    public bool EncryptedState => true;
    public JsonElement Identity => _options.Identity.Clone();

    public static Task<SqliteTerminalPersistenceStore> OpenAsync(SqliteTerminalPersistenceOptions options, CancellationToken cancellationToken = default)
        => OpenCoreAsync(options, cancellationToken, readOnlyCopy: false);

    private static Task<SqliteTerminalPersistenceStore> OpenCoreAsync(SqliteTerminalPersistenceOptions options, CancellationToken cancellationToken, bool readOnlyCopy)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options == null || !options.EnableProfile || options.ReadContext == null || options.KeyProvider == null ||
            options.Mode != StorageOpenMode.Create && options.Mode != StorageOpenMode.Reopen ||
            options.MaxActiveTransfers < 1 || options.MaxActiveTransfers > 32 ||
            options.MaxStagingBytes < 0 || options.MaxStagingBytes > 67108864 ||
            options.MaxReceiptEntries < 0 || options.MaxReceiptEntries > 1048576 ||
            options.MaxTransferFacts < 1 || options.MaxTransferFacts > 262144 ||
            options.MaxObjects < 0 || options.MaxObjects > 1048576 ||
            options.MaxRetainedBytes < 0 || options.MaxRetainedBytes > 1073741824 ||
            options.MaxPages < 32 || options.MaxPages > 1048576) throw new MemoryPublicationRejectedException("invalid_request");
        string path = StorageFileIdentity.FullPath(options.Path);
        var fixedOptions = new SqliteTerminalPersistenceOptions
        {
            EnableProfile = true,
            Path = path,
            Mode = options.Mode,
            Identity = TerminalPersistenceContract.Copy("Identity", options.Identity),
            ReadContext = options.ReadContext,
            KeyProvider = options.KeyProvider,
            AuthorizeRecovery = options.AuthorizeRecovery,
            MaxActiveTransfers = options.MaxActiveTransfers,
            MaxStagingBytes = options.MaxStagingBytes,
            MaxReceiptEntries = options.MaxReceiptEntries,
            MaxTransferFacts = options.MaxTransferFacts,
            MaxObjects = options.MaxObjects,
            MaxRetainedBytes = options.MaxRetainedBytes,
            MaxPages = options.MaxPages,
        };
        string keyId = MemoryPublicationBodyCipher.ReadKeyId(fixedOptions.KeyProvider);
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
        StorageFileIdentity? parent = null, file = null; SqliteConnection? connection = null; SqliteTerminalPersistenceStore? store = null;
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
            var cipher = new MemoryPublicationBodyCipher(fixedOptions.KeyProvider, keyId, metadata);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString()); connection.Open();
            store = new SqliteTerminalPersistenceStore(connection, parent, file, fixedOptions, owner, metadata, cipher);
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
                    store.Exec("INSERT INTO encryption VALUES(1,$check)", ("$check", cipher.Seal("key-check", Array.Empty<byte>())));
                    store.SaveState(new State { ReadOnlyCopy = readOnlyCopy });
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

}
