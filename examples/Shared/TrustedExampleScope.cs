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
    internal string ReadPrincipal()
    {
        var principal = Read().GetProperty("principal").GetString();
        if (string.IsNullOrWhiteSpace(principal)) throw new InvalidOperationException("trusted_principal_required");
        return principal!;
    }
    private JsonElement Read()
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65536) throw new InvalidOperationException("trusted_scope_file_too_large");
        using var output = new MemoryStream(); input.CopyTo(output);
        if (output.Length > 65536) throw new InvalidOperationException("trusted_scope_file_too_large");
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
