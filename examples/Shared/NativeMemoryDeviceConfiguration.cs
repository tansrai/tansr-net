using System.IO;
using System.Text.Json;

namespace Tansr.Examples;

// 只解析用户显式选择的宿主配置；不从模型请求、JWT或会话metadata推断来源身份。
internal sealed class NativeMemoryDeviceConfiguration
{
    private NativeMemoryDeviceConfiguration(JsonElement value)
    {
        if (Text(value, "format") != "tansr-example-device-memory-v1" || !value.GetProperty("enablePreview").GetBoolean())
            throw new InvalidOperationException("device_memory_explicit_preview_required");
        Endpoint = new Uri(Text(value, "serveUrl"), UriKind.Absolute);
        if (!string.IsNullOrEmpty(Endpoint.UserInfo) || !string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
            throw new InvalidOperationException("device_memory_endpoint_must_not_contain_credentials");
        AllowHttp = value.TryGetProperty("allowInsecureLoopback", out var allow) && allow.GetBoolean();
        SessionId = Text(value, "sessionId"); ExecutorId = Text(value, "executorId");
        ScopeFile = AbsolutePath(value, "trustedScopeFile");
        ControllerTokenEnvironment = EnvironmentName(value, "controllerTokenEnvironment");
        DeviceTokenEnvironment = EnvironmentName(value, "deviceTokenEnvironment");
        var workspace = value.GetProperty("workspace"); WorkspacePath = AbsolutePath(workspace, "path");
        WorkspaceId = Text(workspace, "id"); WorkspaceRevision = Text(workspace, "revision");
        var journal = value.GetProperty("journal"); JournalPath = AbsolutePath(journal, "path"); JournalMode = Mode(journal);
        JournalMaxOperations = Positive(journal, "maxOperations"); JournalMaxStoredBytes = Positive(journal, "maxStoredBytes"); JournalMaxPages = Positive(journal, "maxPages");
        var publication = value.GetProperty("publication"); PublicationPath = AbsolutePath(publication, "path"); PublicationMode = Mode(publication);
        PublicationIdentity = publication.GetProperty("identity").Clone();
        MaxTransfers = Positive(publication, "maxTransfers"); MaxStagingBytes = Positive(publication, "maxStagingBytes"); MaxPages = Positive(publication, "maxPages");
        if (string.Equals(JournalPath, PublicationPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ScopeFile, PublicationPath, StringComparison.OrdinalIgnoreCase) || string.Equals(ScopeFile, JournalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("device_memory_paths_must_be_distinct");
    }
    internal Uri Endpoint { get; }
    internal bool AllowHttp { get; }
    internal string SessionId { get; }
    internal string ExecutorId { get; }
    internal string ScopeFile { get; }
    internal string ControllerTokenEnvironment { get; }
    internal string DeviceTokenEnvironment { get; }
    internal string WorkspacePath { get; }
    internal string WorkspaceId { get; }
    internal string WorkspaceRevision { get; }
    internal string JournalPath { get; }
    internal string JournalMode { get; }
    internal int JournalMaxOperations { get; }
    internal long JournalMaxStoredBytes { get; }
    internal int JournalMaxPages { get; }
    internal string PublicationPath { get; }
    internal string PublicationMode { get; }
    internal JsonElement PublicationIdentity { get; }
    internal int MaxTransfers { get; }
    internal int MaxStagingBytes { get; }
    internal int MaxPages { get; }
    internal static async Task<NativeMemoryDeviceConfiguration> LoadAsync(string path, CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(await BoundedFiles.ReadAsync(path, 65536, ct).ConfigureAwait(false));
        return new NativeMemoryDeviceConfiguration(document.RootElement);
    }
    internal static NativeMemoryDeviceConfiguration Parse(JsonElement value) => new(value);
    private static string Text(JsonElement value, string name)
    {
        var text = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw new InvalidOperationException("device_memory_invalid_" + name);
        return text!;
    }
    private static int Positive(JsonElement value, string name)
    { var result = value.GetProperty(name).GetInt32(); if (result <= 0) throw new InvalidOperationException("device_memory_invalid_" + name); return result; }
    private static string AbsolutePath(JsonElement value, string name)
    {
        var path = Text(value, name);
        if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("device_memory_requires_absolute_" + name);
        return path;
    }
    private static string Mode(JsonElement value)
    { var mode = Text(value, "mode"); if (mode != "create" && mode != "reopen") throw new InvalidOperationException("device_memory_explicit_create_or_reopen_required"); return mode; }
    private static string EnvironmentName(JsonElement value, string name)
    {
        var key = Text(value, name);
        if (key.Length > 128 || key.Any(c => !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_'))
            throw new InvalidOperationException("device_memory_invalid_token_environment_name");
        return key;
    }
}
