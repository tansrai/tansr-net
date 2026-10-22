using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Storage;

/// <summary>云端认领未授予本实例执行权；只允许查询原结果，不可转成本地 Pending 后写 unknown。</summary>
public sealed class CloudJournalClaimUnavailableException : InvalidOperationException
{
    public string Disposition { get; }
    internal CloudJournalClaimUnavailableException(string disposition) : base("cloud_journal_claim_" + disposition) => Disposition = disposition;
}

/// <summary>通过独立控制接口保存执行日志，不使用 tool.invoke，也不创建本地文件。
/// 仅首次确认的 granted 可执行。进程丢失 RAM 认领凭据后只能对账，不再次执行副作用。</summary>
public sealed class CloudExecutorJournal : IExecutorJournal
{
    private readonly TansrClient client;
    private readonly string sessionId;
    private readonly Func<JsonElement> connectionProvider;
    private readonly object gate = new();
    private readonly Dictionary<string, Claim> claims = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    private sealed class Claim
    {
        internal readonly JsonElement Operation, Connection;
        internal readonly string AttemptId = Guid.NewGuid().ToString("N");
        internal bool Granted;
        internal string? Token;
        internal Claim(JsonElement operation, JsonElement connection) { Operation = operation; Connection = connection; }
    }

    /// <param name="connectionProvider">读取当前真实注册连接，不可使用模型提供的连接标识。</param>
    public CloudExecutorJournal(TansrClient client, string sessionId, Func<JsonElement> connectionProvider)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        WireJson.ValidateNamed("LegacyId", ExecutionJson.Object(w => w.WriteString("id", sessionId)).GetProperty("id"));
        this.sessionId = sessionId;
    }

    public async Task<ExecutorJournalClaim> ClaimAsync(JsonElement operation, CancellationToken cancellationToken = default)
    {
        operation = operation.Clone();
        var connection = Connection();
        VerifyOperation(operation, connection, current: true);
        var id = ExecutionJson.Text(operation, "operationId");
        Claim claim;
        lock (gate)
        {
            Open();
            if (claims.ContainsKey(id)) throw new CloudJournalClaimUnavailableException("unconfirmed_or_active");
            if (claims.Count >= 128) throw new CloudJournalClaimUnavailableException("capacity");
            claims.Add(id, claim = new Claim(operation, connection));
        }
        // 先在 RAM 记住本次意图；HTTP 失回不删除、不换 attempt，也不自动重发。
        var request = Identity(claim);
        var response = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutionJournalClaim.Path(id: ExecutionJson.Text(connection, "executorId")),
            request, "ExecutionJournalClaimRequest", "ExecutionJournalClaimResponse", cancellationToken).ConfigureAwait(false);
        var record = response.GetProperty("record");
        VerifyRecord(record, connection, operation);
        var disposition = ExecutionJson.Text(response, "disposition");
        if (disposition == "completed")
        {
            ExecutionJson.Check(record.GetProperty("receipt").ValueKind != JsonValueKind.Null);
            lock (gate) claims.Remove(id);
            return ExecutorJournalClaim.Completed(record.GetProperty("receipt"));
        }
        if (disposition != "granted") throw new CloudJournalClaimUnavailableException(disposition);
        ExecutionJson.Check(ExecutionJson.Text(record, "attemptId") == claim.AttemptId &&
            ExecutionJson.Text(record, "connectionId") == ExecutionJson.Text(connection, "connectionId") &&
            ExecutionJson.Text(record, "status") == "pending" && record.GetProperty("receipt").ValueKind == JsonValueKind.Null);
        var currentConnection = Connection();
        SameConnection(connection, currentConnection);
        VerifyOperation(operation, currentConnection, current: true);
        lock (gate) { Open(); claim.Token = ExecutionJson.Text(response, "claimToken"); claim.Granted = true; }
        return ExecutorJournalClaim.Claimed();
    }

    public async Task CompleteAsync(JsonElement operation, JsonElement receipt, CancellationToken cancellationToken = default)
    {
        operation = operation.Clone(); receipt = receipt.Clone();
        ExecutionJson.Receipt(operation, receipt);
        var connection = Connection();
        VerifyOperation(operation, connection, current: false);
        var id = ExecutionJson.Text(operation, "operationId");
        Claim? claim;
        lock (gate) { Open(); claims.TryGetValue(id, out claim); }
        if (claim == null || !claim.Granted)
        {
            // 重启后的原结果对账：已有同一终态可确认，但绝不借读取到的 attemptId 冒充 owner。
            var stored = await ReceiptAsync(operation, cancellationToken).ConfigureAwait(false);
            if (stored.HasValue && ExecutionJson.Equal(stored.Value, receipt)) return;
            throw new CloudJournalClaimUnavailableException("not_owner");
        }
        ExecutionJson.Check(ExecutionJson.Equal(claim.Operation, operation));
        SameConnection(claim.Connection, connection);
        var request = Identity(claim, receipt);
        var response = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutionJournalComplete.Path(id: ExecutionJson.Text(connection, "executorId")),
            request, "ExecutionJournalCompleteRequest", "ExecutionJournalReadResponse", cancellationToken).ConfigureAwait(false);
        var record = response.GetProperty("record");
        VerifyRecord(record, connection, operation);
        ExecutionJson.Check(ExecutionJson.Text(record, "attemptId") == claim.AttemptId &&
            ExecutionJson.Equal(record.GetProperty("receipt"), receipt));
        lock (gate) claims.Remove(id);
    }

    public async Task<JsonElement?> ReceiptAsync(JsonElement operation, CancellationToken cancellationToken = default)
    {
        operation = operation.Clone(); var connection = Connection();
        VerifyOperation(operation, connection, current: false);
        var response = await SendAsync(HttpMethod.Get,
            ApiRoutes.ExecutionJournalRead.Path(id: ExecutionJson.Text(connection, "executorId"), targetId: ExecutionJson.Text(operation, "operationId")) +
            ApiRoutes.ExecutionJournalRead.Query(("protocol", WireContract.Protocol), ("connectionId", ExecutionJson.Text(connection, "connectionId"))),
            null, null, "ExecutionJournalReadResponse", cancellationToken).ConfigureAwait(false);
        var record = response.GetProperty("record");
        if (record.ValueKind == JsonValueKind.Null) return null;
        VerifyRecord(record, connection, operation);
        var receipt = record.GetProperty("receipt");
        if (receipt.ValueKind != JsonValueKind.Null) lock (gate) claims.Remove(ExecutionJson.Text(operation, "operationId"));
        return receipt.ValueKind == JsonValueKind.Null ? null : receipt.Clone();
    }

    public async Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken cancellationToken = default)
    {
        var connection = Connection();
        // 每项包含完整原操作与回执；小页与原 canonical 深度/节点上限共同控制内存，不裁剪记录。
        var query = new List<(string Name, string Value)> { ("protocol", WireContract.Protocol), ("connectionId", ExecutionJson.Text(connection, "connectionId")), ("sessionId", sessionId), ("limit", "16") };
        if (afterOperationId != null) { PersistenceJson.Id(afterOperationId); query.Add(("cursor", afterOperationId)); }
        var response = await SendAsync(HttpMethod.Get, ApiRoutes.ExecutionJournalList.Path(id: ExecutionJson.Text(connection, "executorId")) +
            ApiRoutes.ExecutionJournalList.Query(query.ToArray()),
            null, null, "ExecutionJournalListResponse", cancellationToken).ConfigureAwait(false);
        var result = new List<JsonElement>();
        var previous = afterOperationId;
        foreach (var record in response.GetProperty("records").EnumerateArray())
        {
            VerifyRecord(record, connection);
            var operation = record.GetProperty("operation");
            var id = ExecutionJson.Text(operation, "operationId");
            ExecutionJson.Check(previous == null || string.CompareOrdinal(previous, id) < 0);
            previous = id; result.Add(operation.Clone());
        }
        var cursor = response.GetProperty("nextCursor");
        ExecutionJson.Check(result.Count <= 16 && (cursor.ValueKind == JsonValueKind.Null || result.Count > 0 && cursor.GetString() == previous));
        return result.AsReadOnly();
    }

    public Task CloseAsync()
    {
        lock (gate) { if (closed) return Task.CompletedTask; closed = true; claims.Clear(); }
        lifetime.Cancel(); return Task.CompletedTask;
    }

    private JsonElement Connection()
    {
        lock (gate) Open();
        var connection = connectionProvider().Clone();
        WireJson.ValidateNamed("ExecutorConnection", connection);
        return connection;
    }
    private void Open() { if (closed) throw new ObjectDisposedException(nameof(CloudExecutorJournal)); }
    private static void SameConnection(JsonElement original, JsonElement current) => ExecutionJson.Check(
        ExecutionJson.Text(original, "executorId") == ExecutionJson.Text(current, "executorId") &&
        ExecutionJson.Text(original, "connectionId") == ExecutionJson.Text(current, "connectionId") &&
        ExecutionJson.Text(original, "connectionRevision") == ExecutionJson.Text(current, "connectionRevision"));
    private void VerifyOperation(JsonElement operation, JsonElement connection, bool current)
    {
        ExecutionJson.Operation(operation);
        var scope = client.ReadExecutionScope(); var original = operation.GetProperty("scope");
        ExecutionJson.Check(ExecutionJson.Text(operation, "sessionId") == sessionId &&
            ExecutionJson.Text(scope, "applicationScopeId") == ExecutionJson.Text(original, "applicationScopeId") &&
            ExecutionJson.Text(scope, "endUserId") == ExecutionJson.Text(original, "endUserId"));
        var target = operation.GetProperty("binding").GetProperty("target");
        ExecutionJson.Check(ExecutionJson.Text(target, "executorId") == ExecutionJson.Text(connection, "executorId"));
        if (current) ExecutionJson.Check(ExecutionJson.Equal(scope, original) &&
            ExecutionJson.Text(target, "connectionId") == ExecutionJson.Text(connection, "connectionId") &&
            ExecutionJson.Text(target, "connectionRevision") == ExecutionJson.Text(connection, "connectionRevision"));
    }
    private void VerifyRecord(JsonElement record, JsonElement connection, JsonElement? expected = null)
    {
        WireJson.ValidateNamed("ExecutionJournalRecord", record);
        var operation = record.GetProperty("operation");
        VerifyOperation(operation, connection, current: false);
        if (expected.HasValue) ExecutionJson.Check(ExecutionJson.Equal(operation, expected.Value));
        ExecutionJson.Check(ExecutionJson.Text(record, "connectionId") == ExecutionJson.Text(operation.GetProperty("binding").GetProperty("target"), "connectionId"));
        var receipt = record.GetProperty("receipt"); var status = ExecutionJson.Text(record, "status");
        if (receipt.ValueKind != JsonValueKind.Null)
        { ExecutionJson.Receipt(operation, receipt); ExecutionJson.Check(status == ExecutionJson.Text(receipt, "status")); }
        else ExecutionJson.Check(status == "pending" || status == "unknown");
    }
    private static JsonElement Identity(Claim claim, JsonElement? receipt = null) => ExecutionJson.Object(writer =>
    {
        writer.WriteString("protocol", WireContract.Protocol);
        writer.WriteString("executorId", ExecutionJson.Text(claim.Connection, "executorId"));
        writer.WriteString("connectionId", ExecutionJson.Text(claim.Connection, "connectionId"));
        writer.WriteString("operationId", ExecutionJson.Text(claim.Operation, "operationId"));
        writer.WriteString("digest", ExecutionJson.Text(claim.Operation, "digest"));
        writer.WriteString("attemptId", claim.AttemptId);
        if (receipt.HasValue)
        {
            writer.WriteString("claimToken", claim.Token ?? throw new CloudJournalClaimUnavailableException("not_owner"));
            writer.WritePropertyName("receipt"); receipt.Value.WriteTo(writer);
        }
    });
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonElement? body, string? requestSchema, string responseSchema, CancellationToken ct)
    {
        lock (gate) Open();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        if (body.HasValue && requestSchema != null) WireJson.ValidateNamed(requestSchema, body.Value);
        var result = await client.SendControlAsync(method, path, body, linked.Token, 16 * 1024 * 1024).ConfigureAwait(false);
        WireJson.ValidateNamed(responseSchema, result); linked.Token.ThrowIfCancellationRequested();
        return result;
    }
}
