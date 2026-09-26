using System.IO;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

/// <summary>
/// 原生业务函数的 SDK1 clientTools 示例。仅处理本进程新建的会话；恢复会话没有耐久业务回执，故拒绝副作用。
/// 不冒充 SDK2 的持久 execution host。内存回执仅在当前宿主进程内去重。
/// </summary>
internal sealed class NativeToolHost : IDisposable
{
    private readonly AgentSession _session;
    private readonly string _application;
    private readonly Func<string, CancellationToken, Task> _setTitle;
    private readonly Action<string> _report;
    private readonly object _gate = new();
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeToolBinding> _bindings;
    private bool _stopping;

    internal NativeToolHost(AgentSession session, string application,
        Func<string, CancellationToken, Task> setTitle, Action<string> report, IReadOnlyList<NativeToolBinding>? bindings = null)
    {
        _session = session; _application = application; _setTitle = setTitle; _report = report;
        _bindings = ValidateBindings(bindings).ToDictionary(x => x.Name, StringComparer.Ordinal);
    }

    internal static JsonElement Declarations
    {
        get
        {
            using var document = JsonDocument.Parse("[{\"name\":\"application_info\",\"description\":\"读取当前原生示例应用名称、版本和运行环境；不读取用户文件或凭据。\",\"readOnly\":true,\"timeoutMs\":10000},{\"name\":\"set_window_title\",\"description\":\"设置当前原生应用窗口标题。只影响本应用窗口。\",\"parameters\":{\"title\":{\"type\":\"string\",\"description\":\"不超过120字符的窗口标题\"}},\"readOnly\":false,\"timeoutMs\":10000}]");
            return document.RootElement.Clone();
        }
    }

    internal static JsonElement GetDeclarations(IReadOnlyList<NativeToolBinding>? bindings)
    {
        var optional = ValidateBindings(bindings);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray(); foreach (var item in Declarations.EnumerateArray()) item.WriteTo(writer);
            foreach (var binding in optional) binding.Declaration.WriteTo(writer); writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(buffer.ToArray()); return document.RootElement.Clone();
    }
    private static NativeToolBinding[] ValidateBindings(IReadOnlyList<NativeToolBinding>? bindings)
    {
        var snapshot = bindings?.ToArray() ?? Array.Empty<NativeToolBinding>();
        if (snapshot.Length > 16 || snapshot.Any(x => x == null || x.Name == "application_info" || x.Name == "set_window_title") ||
            snapshot.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("invalid_native_tool_bindings", nameof(bindings));
        return snapshot;
    }

    /// <summary>不得 await 工具执行来阻塞 SSE 读取；取消帧需继续抵达。</summary>
    internal void HandleEvent(AgentEvent item)
    {
        var type = Text(item.Data, "type") ?? item.Name;
        if (type != "server.tool.request" && type != "server.tool.cancel") return;
        var data = item.Data.TryGetProperty("payload", out var payload) ? payload : item.Data;
        var id = Text(data, "callId"); if (id == null) return;
        lock (_gate)
        {
            if (_stopping) return;
            if (type == "server.tool.cancel") { if (_calls.TryGetValue(id, out var cancelled)) cancelled.Stop.Cancel(); return; }
            if (_calls.TryGetValue(id, out var previous))
            {
                if (Text(data, "name") != Text(previous.Data, "name") || ArgumentText(data) != ArgumentText(previous.Data))
                { Report("tool_request_conflict"); return; }
                // 同一观察连接会丢弃重复 seq；重放仅重送已记录的原回执，不再调用委托。
                if (previous.Receipt.HasValue && previous.Task.IsCompleted) previous.Task = SubmitAsync(id, previous.Receipt.Value, CancellationToken.None);
                return;
            }
            if (_calls.Count >= 256) { Report("native_tool_host_capacity: start a new session"); return; }
            var call = new Call(id, data.Clone()); _calls[id] = call;
            call.Task = ExecuteAsync(call, Integer(item.Data, "ts"));
        }
    }

    private async Task ExecuteAsync(Call call, long? eventTime)
    {
        await Task.Yield();
        JsonElement receipt;
        try
        {
            if (_session.Resumed) throw new InvalidOperationException("native_tool_host_requires_new_session");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var deadline = Integer(call.Data, "deadlineAt"); var ttl = Integer(call.Data, "ttlMs");
            // 示例没有可信时钟校准：超过5秒偏差保守拒绝，不把重放TTL重新变成执行许可。
            if (!eventTime.HasValue || Math.Abs((decimal)eventTime.Value - now) > 5000 ||
                !deadline.HasValue || !ttl.HasValue || ttl <= 0 || ttl > 60000 || deadline <= now)
                throw new InvalidOperationException("tool_clock_or_expiry_unconfirmed");
            call.Stop.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(ttl.Value, deadline.Value - now)));
            call.Stop.Token.ThrowIfCancellationRequested();
            var name = Text(call.Data, "name");
            string result;
            if (name != null && _bindings.TryGetValue(name, out var binding))
            {
                if (!call.Data.TryGetProperty("args", out var arguments) || arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("invalid_tool_arguments");
                receipt = await binding.ExecuteAsync(arguments, call.Stop.Token).ConfigureAwait(false);
                // 使用同一个调用记录保存原回执；后续重放只能重送，不重做MCP请求。
                if (receipt.ValueKind != JsonValueKind.Object || Text(receipt, "status") is not ("ok" or "error")) throw new InvalidOperationException("invalid_native_tool_receipt");
                receipt = receipt.Clone();
            }
            else
            {
                if (name == "application_info")
                    result = _application + "; version=" + typeof(NativeToolHost).Assembly.GetName().Version + "; runtime=" + Environment.Version + "; os=" + Environment.OSVersion.Platform;
                else if (name == "set_window_title")
                {
                    if (!call.Data.TryGetProperty("args", out var arguments)) throw new InvalidOperationException("invalid_tool_arguments");
                    var title = Text(arguments, "title");
                    if (string.IsNullOrWhiteSpace(title) || title!.Length > 120 || title.Any(char.IsControl)) throw new InvalidOperationException("invalid_window_title");
                    await _setTitle(title, call.Stop.Token).ConfigureAwait(false);
                    result = "window_title_updated";
                }
                else throw new InvalidOperationException("unknown_native_tool");
                receipt = Receipt(result, false);
            }
        }
        catch (OperationCanceledException) { receipt = Receipt("native_tool_cancelled", true); }
        catch (Exception error) { receipt = Receipt(error is InvalidOperationException ? error.Message : "native_tool_failed", true); }
        lock (_gate) call.Receipt = receipt;
        await SubmitAsync(call.Id, receipt, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SubmitAsync(string id, JsonElement receipt, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await _session.SubmitToolResultAsync(id, receipt, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) { Report("tool_receipt_unconfirmed:" + (error is TansrException sdk ? sdk.Code : error.GetType().Name)); }
    }

    internal async Task DrainAsync(CancellationToken token)
    {
        Task[] tasks;
        lock (_gate) { _stopping = true; foreach (var call in _calls.Values) call.Stop.Cancel(); tasks = _calls.Values.Select(x => x.Task).ToArray(); }
        var all = Task.WhenAll(tasks);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        { if (await Task.WhenAny(all, cancelled.Task).ConfigureAwait(false) != all) throw new OperationCanceledException(token); }
        await all.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            foreach (var call in _calls.Values) { call.Stop.Cancel(); if (call.Task.IsCompleted) call.Stop.Dispose(); }
        }
    }
    private void Report(string code) { try { _report(code); } catch { /* 宿主日志故障不得重跑工具。 */ } }
    private static string ArgumentText(JsonElement data) => data.TryGetProperty("args", out var args) ? args.GetRawText() : "null";
    private static string? Text(JsonElement data, string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Integer(JsonElement data, string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static JsonElement Receipt(string result, bool error)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("status", error ? "error" : "ok");
            if (error) writer.WriteString("message", result);
            else { writer.WriteStartArray("content"); writer.WriteStartObject(); writer.WriteString("t", "text"); writer.WriteString("text", result); writer.WriteEndObject(); writer.WriteEndArray(); }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray()); return document.RootElement.Clone();
    }
    private sealed class Call(string id, JsonElement data)
    { internal readonly string Id = id; internal readonly JsonElement Data = data; internal readonly CancellationTokenSource Stop = new(); internal Task Task = System.Threading.Tasks.Task.CompletedTask; internal JsonElement? Receipt; }
}

/// <summary>只由应用装配创建；声明不是从MCP发现结果生成的授权。</summary>
internal sealed class NativeToolBinding
{
    internal NativeToolBinding(JsonElement declaration, Func<JsonElement, CancellationToken, Task<JsonElement>> execute)
    {
        if (declaration.ValueKind != JsonValueKind.Object || !declaration.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 128)
            throw new ArgumentException("invalid_native_tool_declaration", nameof(declaration));
        Name = name.GetString()!; Declaration = declaration.Clone(); ExecuteAsync = execute ?? throw new ArgumentNullException(nameof(execute));
    }
    internal string Name { get; }
    internal JsonElement Declaration { get; }
    internal Func<JsonElement, CancellationToken, Task<JsonElement>> ExecuteAsync { get; }
}
