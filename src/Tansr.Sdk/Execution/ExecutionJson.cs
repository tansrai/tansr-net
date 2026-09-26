using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Execution;

internal static class ExecutionJson
{
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    internal static bool Equal(JsonElement left, JsonElement right) =>
        WireJson.EncodeControl(left, 1048576).SequenceEqual(WireJson.EncodeControl(right, 1048576));

    internal static JsonElement Object(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return WireJson.Parse(stream.ToArray(), 1048576);
    }

    internal static void Check(bool condition)
    {
        if (!condition) throw new InvalidDataException("执行协议身份或内容不一致。");
    }

    internal static void Operation(JsonElement operation)
    {
        WireJson.ValidateNamed("ExecutionOperation", operation);
        var payload = Object(writer =>
        {
            foreach (var field in operation.EnumerateObject())
            {
                if (field.Name == "digest") continue;
                writer.WritePropertyName(field.Name);
                field.Value.WriteTo(writer);
            }
        });
        Check(WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(payload, 1048576)) == Text(operation, "digest"));
        var request = operation.GetProperty("request");
        var args = request.GetProperty("args");
        if (Text(request, "operation") == "process.exec")
            Check(Equal(operation.GetProperty("binding").GetProperty("target").GetProperty("interpreter"), args.GetProperty("interpreter")));
        if (Text(request, "operation") == "tool.invoke")
        {
            Check(Text(operation, "toolName") == Text(args, "name"));
            var json = System.Text.Encoding.UTF8.GetBytes(Text(args, "argsJson"));
            Check(WireJson.Parse(json, 32768, 32).ValueKind == JsonValueKind.Object);
        }
    }

    internal static void Receipt(JsonElement operation, JsonElement receipt)
    {
        ValidateReceiptRequest(receipt);
        var target = operation.GetProperty("binding").GetProperty("target");
        Check(Text(receipt, "operationId") == Text(operation, "operationId") && Text(receipt, "digest") == Text(operation, "digest") &&
              Text(receipt, "executorId") == Text(target, "executorId") && Text(receipt, "connectionId") == Text(target, "connectionId"));
        var completed = Text(receipt, "status") == "completed";
        var result = receipt.GetProperty("result");
        if (!completed) return;
        var request = operation.GetProperty("request");
        Check(Text(result, "operation") == Text(request, "operation"));
        var args = result.GetProperty("args");
        var asked = request.GetProperty("args");
        switch (Text(result, "operation"))
        {
            case "fs.read":
                Check(WireJson.DecodeBase64(Text(args, "bytesBase64")).Length <= asked.GetProperty("length").GetInt32());
                break;
            case "fs.write":
                Check(Text(args, "hash") == WireJson.Sha256(WireJson.DecodeBase64(Text(asked, "bytesBase64"))));
                break;
            case "process.exec":
                Check(System.Text.Encoding.UTF8.GetByteCount(Text(args, "stdout")) + System.Text.Encoding.UTF8.GetByteCount(Text(args, "stderr")) <= asked.GetProperty("maxOutputBytes").GetInt32());
                break;
        }
    }

    internal static void ValidateReceiptRequest(JsonElement receipt)
    {
        WireJson.ValidateNamed("ExecutionReceiptRequest", receipt);
        var completed = Text(receipt, "status") == "completed";
        var result = receipt.GetProperty("result");
        Check(completed ? result.ValueKind != JsonValueKind.Null && receipt.GetProperty("errorCode").ValueKind == JsonValueKind.Null :
            result.ValueKind == JsonValueKind.Null && receipt.GetProperty("errorCode").ValueKind == JsonValueKind.String);
        if (!completed) return;
        var args = result.GetProperty("args");
        switch (Text(result, "operation"))
        {
            case "fs.read":
                Check(WireJson.DecodeBase64(Text(args, "bytesBase64")).Length <= 65536);
                break;
            case "fs.list":
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in args.GetProperty("entries").EnumerateArray())
                {
                    var name = Text(entry, "name");
                    Check(names.Add(name) && name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', '\0' }) < 0);
                }
                break;
            case "tool.invoke":
                ToolReceipt(Text(args, "resultJson"));
                break;
            case "process.exec":
                if (args.GetProperty("exitCode").ValueKind != JsonValueKind.Null)
                    Check(int.TryParse(Text(args, "exitCode"), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _));
                break;
        }
    }

    private static void ToolReceipt(string text)
    {
        var value = WireJson.Parse(System.Text.Encoding.UTF8.GetBytes(text), 32768, 32);
        Check(value.ValueKind == JsonValueKind.Object);
        if (Text(value, "status") == "error")
        {
            var message = Text(value, "message");
            Check(message.Length >= 1 && message.Length <= 4096);
            return;
        }
        Check(Text(value, "status") == "ok");
        var content = value.GetProperty("content");
        Check(content.ValueKind == JsonValueKind.Array && content.GetArrayLength() >= 1 && content.GetArrayLength() <= 64);
        if (value.TryGetProperty("isError", out var isError)) Check(isError.ValueKind == JsonValueKind.True || isError.ValueKind == JsonValueKind.False);
        foreach (var item in content.EnumerateArray())
        {
            Check(item.ValueKind == JsonValueKind.Object);
            if (Text(item, "t") == "text") Check(item.GetProperty("text").ValueKind == JsonValueKind.String);
            else
            {
                Check(Text(item, "t") == "image" && item.GetProperty("data").ValueKind == JsonValueKind.String);
                var mime = Text(item, "mime");
                Check(mime == "image/png" || mime == "image/jpeg" || mime == "image/webp" || mime == "image/gif");
            }
        }
    }

    internal static JsonElement ReceiptFor(JsonElement operation, string status, JsonElement? result, string? error) => Object(writer =>
    {
        writer.WriteString("protocol", "sdk2-ext-v1");
        var target = operation.GetProperty("binding").GetProperty("target");
        writer.WriteString("executorId", Text(target, "executorId"));
        writer.WriteString("connectionId", Text(target, "connectionId"));
        writer.WriteString("operationId", Text(operation, "operationId"));
        writer.WriteString("digest", Text(operation, "digest"));
        writer.WriteString("status", status);
        writer.WritePropertyName("result");
        if (result.HasValue) result.Value.WriteTo(writer); else writer.WriteNullValue();
        writer.WriteString("errorCode", error);
    });
}
