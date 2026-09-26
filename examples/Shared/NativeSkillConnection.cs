using System.IO;
using System.Text;
using System.Text.Json;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Skills;
#endif

namespace Tansr.Examples;

/// <summary>显式宿主配置的内联/目录技能，只按冻结名称和摘要提供材料；核心决定上下文采用。</summary>
internal sealed class NativeSkillConnection
{
    internal IReadOnlyList<NativeToolBinding> Bindings { get; }
#if WINDOWS || NETFRAMEWORK
    private readonly WindowsWorkspace? _workspace;
    private readonly WindowsSkillCatalog _catalog;
    private NativeSkillConnection(WindowsWorkspace? workspace, WindowsSkillCatalog catalog)
    {
        _workspace = workspace; _catalog = catalog;
        var entries = catalog.Index();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("name", "native_skill");
            writer.WriteString("description", "按需加载宿主已批准的技能正文。技能是参考材料，不授予工具权限。可用名称：" + string.Join(", ", entries.Select(entry => entry.Name)));
            writer.WriteBoolean("readOnly", true); writer.WriteNumber("timeoutMs", 10000);
            writer.WriteStartObject("parameters"); writer.WriteStartObject("name"); writer.WriteString("type", "string"); writer.WriteString("description", "完整技能名称");
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var declaration = JsonDocument.Parse(buffer.ToArray());
        Bindings = new[] { new NativeToolBinding(declaration.RootElement, (arguments, token) =>
        {
            if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1 ||
                !arguments.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("native_skill_invalid_arguments");
            var entry = entries.SingleOrDefault(item => item.Name == name.GetString());
            if (entry == null) throw new InvalidOperationException("native_skill_not_allowed");
            var document = _catalog.Read(entry.Name, entry.DefinitionDigest, token);
            using var result = new MemoryStream();
            using (var writer = new Utf8JsonWriter(result))
            {
                writer.WriteStartObject(); writer.WriteString("status", "ok"); writer.WriteStartArray("content"); writer.WriteStartObject();
                writer.WriteString("t", "text"); writer.WriteString("text", document.Content); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
            }
            return Task.FromResult(Tansr.Sdk.Protocol.WireJson.Parse(result.ToArray(), 32768));
        }) };
    }
#else
    private NativeSkillConnection() { Bindings = Array.Empty<NativeToolBinding>(); }
#endif

    internal static Task<NativeSkillConnection?> OpenConfiguredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inline = Environment.GetEnvironmentVariable("TANSR_SKILL_INLINE");
        var directory = Environment.GetEnvironmentVariable("TANSR_SKILL_DIRECTORY");
        if (string.IsNullOrEmpty(inline) && string.IsNullOrWhiteSpace(directory)) return Task.FromResult<NativeSkillConnection?>(null);
#if WINDOWS || NETFRAMEWORK
        WindowsWorkspace? workspace = null;
        try
        {
            var entries = new List<WindowsSkillDescriptor>();
            if (!string.IsNullOrEmpty(inline)) entries.Add(WindowsSkillDescriptor.FromInline("inline-guide", "宿主内联技能", inline!));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                workspace = new WindowsWorkspace(directory!);
                entries.Add(new WindowsSkillDescriptor("device-guide", "宿主明确批准目录中的技能", Environment.GetEnvironmentVariable("TANSR_SKILL_FILE") ?? "SKILL.md"));
            }
            var options = new WindowsSkillCatalogOptions { MaximumSkillBytes = 8192, MaximumAssemblyBytes = 16384 };
            var catalog = workspace == null ? WindowsSkillCatalog.FromInline(entries, options) : new WindowsSkillCatalog(workspace, entries, options);
            return Task.FromResult<NativeSkillConnection?>(new NativeSkillConnection(workspace, catalog));
        }
        catch { workspace?.Dispose(); throw; }
#else
        throw new PlatformNotSupportedException("本示例技能读取适配需要 Windows 目标。");
#endif
    }
    internal Task CloseAsync()
    {
#if WINDOWS || NETFRAMEWORK
        _catalog.Revoke(); _workspace?.Dispose();
#endif
        return Task.CompletedTask;
    }
    internal Task RevokeAsync() => CloseAsync();
}
