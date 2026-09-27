using System.IO;
using System.Text.Json;

namespace Tansr.Examples;

// 文件由应用宿主可信配置提供，不能从聊天、模型参数或未验证 JWT 生成。
// 此处只传递宿主声明；实际认证和权限仍由 Serve 核对。
internal sealed class TrustedExampleScope
{
    private readonly string path;
    private TrustedExampleScope(string path) => this.path = Path.GetFullPath(path);
    internal static TrustedExampleScope FromPath(string path) => new(path);
    internal static TrustedExampleScope? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("TANSR_TRUSTED_SCOPE_FILE");
        return string.IsNullOrWhiteSpace(path) ? null : new TrustedExampleScope(path!);
    }
    internal JsonElement ReadScope() => Read().GetProperty("scope").Clone();
    internal static string? CurrentPresentationIdentity() => FromEnvironment()?.ReadPresentationIdentity();
    internal string ReadPresentationIdentity()
    {
        // One trusted snapshot: a file replacement cannot mix two users' fields.
        var value = Read(); var scope = value.GetProperty("scope");
        static string Required(JsonElement element, string name)
        {
            var field = element.GetProperty(name);
            var text = field.ValueKind == JsonValueKind.String ? field.GetString() : null;
            if (string.IsNullOrWhiteSpace(text) || text!.Length > 1024 || text.Any(char.IsControl))
                throw new InvalidOperationException("trusted_presentation_identity_required");
            return text;
        }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray(); writer.WriteStringValue(Required(value, "principal"));
            writer.WriteStringValue(Required(scope, "applicationScopeId")); writer.WriteStringValue(Required(scope, "endUserId")); writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    internal string ReadPrincipal()
    {
        var principal = Read().GetProperty("principal").GetString();
        if (string.IsNullOrWhiteSpace(principal)) throw new InvalidOperationException("trusted_principal_required");
        return principal!;
    }
    private JsonElement Read()
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 65536) throw new InvalidOperationException("trusted_scope_file_too_large");
        using var output = new MemoryStream(); input.CopyTo(output);
        if (output.Length > 65536) throw new InvalidOperationException("trusted_scope_file_too_large");
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
