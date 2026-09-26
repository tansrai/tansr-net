using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Execution;

internal static class ExecutionFixture
{
    internal static readonly byte[] Content = [1, 2, 3, 4];
    internal static JsonElement Scope(string user = "user", string revision = "1", string application = "app") => JsonSerializer.SerializeToElement(new
    {
        applicationScopeId = application,
        endUserId = user,
        authorizationRevision = revision
    });

    internal static JsonElement Registration() => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        executorId = "executor-1",
        platform = new { platform = "windows", arch = "x64", language = "csharp", runtimeVersion = "10", adapterVersion = "1" },
        workspaces = new[] { new { workspaceId = "workspace-1", revision = "1" } },
        operations = new[] { "fs.write" }
    });

    internal static JsonElement Connection(string revision = "1") => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        executorId = "executor-1",
        connectionId = "connection-1",
        connectionRevision = revision,
        expiresAt = Future(),
        heartbeatAfterMs = 30000
    });

    internal static JsonElement Operation(string id = "operation-1", string user = "user", string authorization = "1",
        string connectionRevision = "1", string workspace = "workspace-1", string workspaceRevision = "1", string application = "app")
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            protocol = "sdk2-ext-v1",
            operationId = id,
            sessionId = "session-1",
            scope = Scope(user, authorization, application),
            binding = new
            {
                bindingId = "binding-1",
                revision = "1",
                target = new { executorId = "executor-1", connectionId = "connection-1", connectionRevision, workspaceId = workspace, workspaceRevision }
            },
            toolName = "Write",
            request = new { operation = "fs.write", args = new { path = "synthetic.txt", expectedHash = (string?)null, bytesBase64 = Convert.ToBase64String(Content) } },
            expiresAt = Future()
        });
        var signed = Set(body, "digest", JsonSerializer.SerializeToElement(WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(body, 1048576))));
        WireJson.ValidateNamed("ExecutionOperation", signed);
        return signed;
    }

    internal static JsonElement Batch(params JsonElement[] operations) => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        executorId = "executor-1",
        connectionId = "connection-1",
        operations
    });

    internal static JsonElement Result() => JsonSerializer.SerializeToElement(new
    {
        operation = "fs.write",
        args = new { hash = WireJson.Sha256(Content) }
    });

    internal static JsonElement Receipt(JsonElement operation, string status = "completed") => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        executorId = "executor-1",
        connectionId = "connection-1",
        operationId = operation.GetProperty("operationId").GetString(),
        digest = operation.GetProperty("digest").GetString(),
        status,
        result = status == "completed" ? Result() : (JsonElement?)null,
        errorCode = status == "completed" ? null : "execution_outcome_unknown"
    });

    internal static JsonElement Status(JsonElement operation, JsonElement? receipt) => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        operation,
        status = receipt.HasValue ? receipt.Value.GetProperty("status").GetString() : "pending",
        receipt
    });

    internal static JsonElement Set(JsonElement value, string name, JsonElement replacement)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name != name) property.WriteTo(writer);
            }
            writer.WritePropertyName(name); replacement.WriteTo(writer);
            writer.WriteEndObject();
        }
        return WireJson.Parse(stream.ToArray(), 1048576);
    }

    internal static bool Same(JsonElement left, JsonElement right) => WireJson.CanonicalString(left, 1048576) == WireJson.CanonicalString(right, 1048576);
    private static string Future() => DateTimeOffset.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
