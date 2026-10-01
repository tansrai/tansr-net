using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Execution;

/// <summary>已冻结执行端点的客户端；与会话客户端共用认证和主体守卫。</summary>
public sealed class ExecutionClient : IDeviceExecutionClient
{
    private readonly TansrClient _client;
    public ExecutionClient(TansrClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));
    public JsonElement ReadScope() => _client.ReadExecutionScope();

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonElement? input, string? requestSchema, string responseSchema, CancellationToken ct)
    {
        if (input.HasValue && requestSchema != null) WireJson.ValidateNamed(requestSchema, input.Value);
        var value = await _client.SendControlAsync(method, path, input, ct).ConfigureAwait(false);
        WireJson.ValidateNamed(responseSchema, value);
        return value;
    }

    /// <summary>声明平台及所需工具；不因客户端声明而增加应用授权。</summary>
    public async Task<JsonElement> InitializeAsync(JsonElement initialization, CancellationToken cancellationToken = default)
    {
        var input = Snapshot("SessionInitializeRequest", initialization);
        if (input.TryGetProperty("requestedTools", out var requested)) Unique(requested);
        var sessionId = ExecutionJson.Text(input, "sessionId");
        var response = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutionInitialize.Path(id: sessionId), input,
            "SessionInitializeRequest", "SessionExecutionCapabilities", cancellationToken).ConfigureAwait(false);
        VerifyCapabilities(response, sessionId);
        ExecutionJson.Check(ExecutionJson.Equal(response.GetProperty("platform"), input.GetProperty("platform")));
        return response;
    }

    public async Task<JsonElement> GetExecutionCapabilitiesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, ApiRoutes.ExecutionCapabilities.Path(id: sessionId) + ApiRoutes.ExecutionCapabilities.Query(("protocol", WireContract.Protocol)), null,
            null, "SessionExecutionCapabilities", cancellationToken).ConfigureAwait(false);
        VerifyCapabilities(response, sessionId);
        return response;
    }

    /// <summary>读取应用策略、终端能力和当前绑定的分层事实，不授予执行权。</summary>
    public async Task<JsonElement> GetExecutionBoundaryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, ApiRoutes.ExecutionBoundary.Path(id: sessionId) + ApiRoutes.ExecutionBoundary.Query(("protocol", WireContract.Protocol)), null,
            null, "ExecutionBoundary", cancellationToken).ConfigureAwait(false);
        ExecutionJson.Check(ExecutionJson.Text(response, "sessionId") == sessionId);
        var application = response.GetProperty("application");
        Unique(application.GetProperty("policyTools"));
        Unique(application.GetProperty("platformCapabilities"));
        Unique(response.GetProperty("effectiveTools"), "name");
        if (response.GetProperty("requestedTools").ValueKind != JsonValueKind.Null) Unique(response.GetProperty("requestedTools"));
        var executor = response.GetProperty("executor");
        if (executor.ValueKind != JsonValueKind.Null)
        {
            Unique(executor.GetProperty("operations"));
            Unique(executor.GetProperty("workspaces"), "workspaceId");
            var binding = response.GetProperty("binding");
            ExecutionJson.Check(binding.ValueKind != JsonValueKind.Null);
            var target = binding.GetProperty("target");
            ExecutionJson.Check(ExecutionJson.Text(target, "executorId") == ExecutionJson.Text(executor, "executorId") &&
                ExecutionJson.Text(target, "connectionId") == ExecutionJson.Text(executor, "connectionId") &&
                ExecutionJson.Text(target, "connectionRevision") == ExecutionJson.Text(executor, "connectionRevision"));
        }
        return response;
    }

    /// <summary>使用已核验的完整目标（含连接/工作区代际及解释器）校对绑定；只比较 ID 不足以确认正确设备。</summary>
    public async Task<JsonElement> BindExecutionAsync(JsonElement request, JsonElement expectedTarget, CancellationToken cancellationToken = default)
    {
        var input = Snapshot("ExecutionBindingRequest", request);
        var target = Snapshot("ExecutionTarget", expectedTarget);
        ExecutionJson.Check(ExecutionJson.Text(input, "executorId") == ExecutionJson.Text(target, "executorId") &&
            ExecutionJson.Text(input, "connectionId") == ExecutionJson.Text(target, "connectionId") &&
            ExecutionJson.Text(input, "workspaceId") == ExecutionJson.Text(target, "workspaceId"));
        var sessionId = ExecutionJson.Text(input, "sessionId");
        var response = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutionBindingCreate.Path(id: sessionId), input,
            "ExecutionBindingRequest", "SessionExecutionCapabilities", cancellationToken).ConfigureAwait(false);
        VerifyCapabilities(response, sessionId);
        var binding = response.GetProperty("binding");
        ExecutionJson.Check(binding.ValueKind != JsonValueKind.Null && ExecutionJson.Equal(binding.GetProperty("target"), target));
        return response;
    }

    public async Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken cancellationToken = default)
    {
        var input = Snapshot("ExecutorRegistrationRequest", registration);
        Unique(input.GetProperty("operations"));
        Unique(input.GetProperty("workspaces"), "workspaceId");
        var hasInvoke = input.GetProperty("operations").EnumerateArray().Any(item => item.GetString() == "tool.invoke");
        var hasTools = input.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0;
        if (input.TryGetProperty("tools", out tools)) Unique(tools, "name");
        ExecutionJson.Check(hasInvoke == hasTools);
        var response = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutorRegister.Path(), input, "ExecutorRegistrationRequest", "ExecutorConnection", cancellationToken).ConfigureAwait(false);
        ExecutionJson.Check(ExecutionJson.Text(response, "executorId") == ExecutionJson.Text(input, "executorId"));
        return response;
    }

    public async Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken cancellationToken = default)
    {
        connection = Snapshot("ExecutorConnection", connection);
        var request = ExecutionJson.Object(writer =>
        {
            writer.WriteString("protocol", "sdk2-ext-v1");
            writer.WriteString("executorId", ExecutionJson.Text(connection, "executorId"));
            writer.WriteString("connectionId", ExecutionJson.Text(connection, "connectionId"));
        });
        var result = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutorHeartbeat.Path(id: ExecutionJson.Text(connection, "executorId")), request, "ExecutorHeartbeatRequest", "ExecutorConnection", cancellationToken).ConfigureAwait(false);
        ExecutionJson.Check(ExecutionJson.Text(result, "executorId") == ExecutionJson.Text(connection, "executorId") && ExecutionJson.Text(result, "connectionId") == ExecutionJson.Text(connection, "connectionId"));
        return result;
    }

    public async Task<JsonElement> PollAsync(JsonElement connection, CancellationToken cancellationToken = default)
    {
        connection = Snapshot("ExecutorConnection", connection);
        var scope = ReadScope();
        var result = await SendAsync(HttpMethod.Get, ApiRoutes.ExecutorOperationsPoll.Path(id: ExecutionJson.Text(connection, "executorId")) + ApiRoutes.ExecutorOperationsPoll.Query(("protocol", WireContract.Protocol), ("connectionId", ExecutionJson.Text(connection, "connectionId"))), null, null, "ExecutionBatch", cancellationToken).ConfigureAwait(false);
        ExecutionJson.Check(ExecutionJson.Equal(scope, ReadScope()));
        ExecutionJson.Check(ExecutionJson.Text(result, "executorId") == ExecutionJson.Text(connection, "executorId") && ExecutionJson.Text(result, "connectionId") == ExecutionJson.Text(connection, "connectionId"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in result.GetProperty("operations").EnumerateArray())
        {
            ExecutionJson.Operation(operation);
            var target = operation.GetProperty("binding").GetProperty("target");
            ExecutionJson.Check(seen.Add(ExecutionJson.Text(operation, "operationId")) && ExecutionJson.Equal(scope, operation.GetProperty("scope")) &&
                ExecutionJson.Text(target, "executorId") == ExecutionJson.Text(connection, "executorId") &&
                ExecutionJson.Text(target, "connectionId") == ExecutionJson.Text(connection, "connectionId") &&
                ExecutionJson.Text(target, "connectionRevision") == ExecutionJson.Text(connection, "connectionRevision"));
        }
        return result;
    }

    /// <summary>原回执补投可以携带过去授权代的执行事实；核对历史不代表重新授权执行该操作。</summary>
    public async Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken cancellationToken = default)
    {
        receipt = Snapshot("ExecutionReceiptRequest", receipt);
        ExecutionJson.ValidateReceiptRequest(receipt);
        var scope = ReadScope();
        var result = await SendAsync(HttpMethod.Post, ApiRoutes.ExecutorReceiptSubmit.Path(id: ExecutionJson.Text(receipt, "executorId")), receipt, "ExecutionReceiptRequest", "ExecutionStatus", cancellationToken).ConfigureAwait(false);
        ExecutionJson.Check(ExecutionJson.Equal(scope, ReadScope()));
        VerifyStatus(result, scope);
        ExecutionJson.Receipt(result.GetProperty("operation"), receipt);
        ExecutionJson.Check(ExecutionJson.Equal(result.GetProperty("receipt"), receipt) && ExecutionJson.Text(result, "status") == ExecutionJson.Text(receipt, "status"));
        return result;
    }

    public async Task<JsonElement> GetStatusAsync(string sessionId, string operationId, CancellationToken cancellationToken = default)
    {
        var scope = ReadScope();
        var result = await SendAsync(HttpMethod.Get, ApiRoutes.ExecutionStatus.Path(id: sessionId, targetId: operationId) + ApiRoutes.ExecutionStatus.Query(("protocol", WireContract.Protocol)), null, null, "ExecutionStatus", cancellationToken).ConfigureAwait(false);
        var operation = result.GetProperty("operation");
        ExecutionJson.Check(ExecutionJson.Equal(scope, ReadScope()));
        ExecutionJson.Check(ExecutionJson.Text(operation, "sessionId") == sessionId && ExecutionJson.Text(operation, "operationId") == operationId);
        VerifyStatus(result, scope);
        return result;
    }

    private static JsonElement Snapshot(string schema, JsonElement value)
    {
        var copy = value.Clone();
        WireJson.ValidateNamed(schema, copy);
        return copy;
    }

    private static void Unique(JsonElement array, string? property = null)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
            ExecutionJson.Check(values.Add(property == null ? item.GetString()! : ExecutionJson.Text(item, property)));
    }

    private static void VerifyCapabilities(JsonElement response, string sessionId)
    {
        ExecutionJson.Check(ExecutionJson.Text(response, "sessionId") == sessionId);
        Unique(response.GetProperty("effectiveTools"), "name");
    }

    private static void VerifyStatus(JsonElement result, JsonElement scope)
    {
        var operation = result.GetProperty("operation");
        ExecutionJson.Operation(operation);
        // 历史事实只要求仍属于同一 app/user；旧授权修订绝不能用于 Poll 的当前派工校验。
        ExecutionJson.Check(
            ExecutionJson.Text(scope, "applicationScopeId") == ExecutionJson.Text(operation.GetProperty("scope"), "applicationScopeId") &&
            ExecutionJson.Text(scope, "endUserId") == ExecutionJson.Text(operation.GetProperty("scope"), "endUserId"));
        if (result.GetProperty("receipt").ValueKind != JsonValueKind.Null)
        {
            ExecutionJson.Receipt(operation, result.GetProperty("receipt"));
            ExecutionJson.Check(ExecutionJson.Text(result, "status") == ExecutionJson.Text(result.GetProperty("receipt"), "status"));
        }
        else ExecutionJson.Check(ExecutionJson.Text(result, "status") == "pending" || ExecutionJson.Text(result, "status") == "unknown");
    }
}
