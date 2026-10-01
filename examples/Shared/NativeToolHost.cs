using System.Globalization;
using System.IO;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Hosting;
#endif

namespace Tansr.Examples;

/// <summary>
/// 原生业务函数的 SDK1 clientTools 示例。恢复时固定权威会话水位，只接手其后新签发的请求。
/// 水位之前的未知副作用不重做；恢复历史不等于恢复了业务回执。
/// 旧桥的内存回执仅在当前宿主进程内去重；设备模式将相同委托交原持久 execution host。
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
    private readonly CancellationTokenSource _lifetime = new();
    private long? _resumeBoundary;
    private long _retiredThrough = -1;
    private bool _stopping;
    private bool _deviceExecution = false;

    internal NativeToolHost(AgentSession session, string application,
        Func<string, CancellationToken, Task> setTitle, Action<string> report, IReadOnlyList<NativeToolBinding>? bindings = null)
        : this(session, application, setTitle, report, bindings, false) { }

    internal NativeToolHost(AgentSession session, string application,
        Func<string, CancellationToken, Task> setTitle, Action<string> report, IReadOnlyList<NativeToolBinding>? bindings, bool resumeRequested)
    {
        _session = session; _application = application; _setTitle = setTitle; _report = report;
        _bindings = ValidateBindings(bindings).ToDictionary(x => x.Name, StringComparer.Ordinal);
        // A live attach returns resumed:false on the original wire. The host still
        // attaches to existing history and must not execute an unobserved prior call.
        Ready = session.Resumed || resumeRequested ? InitializeResumeAsync(session.LastSequence) : Task.CompletedTask;
        _ = Ready.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>恢复宿主必须先完成这一次权威水位读取，再允许用户发送新一轮；不会自动重试或重建会话。</summary>
    internal Task Ready { get; }
    private async Task InitializeResumeAsync(long creationSequence)
    {
        var metadata = await _session.ReadMetadataAsync(_lifetime.Token).ConfigureAwait(false);
        if (!metadata.IsLive) throw new InvalidOperationException("native_tool_resume_not_live");
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            _resumeBoundary = Math.Max(creationSequence, metadata.LastSequence);
        }
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

#if WINDOWS || NETFRAMEWORK
    /// <summary>由明确的设备连接操作选择原持久执行管道；同一宿主不再回落执行旧 SSE 请求。</summary>
    internal IReadOnlyList<WindowsBusinessTool> SelectDeviceExecution()
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("native_tool_host_stopping");
            if (!Ready.IsCompleted || Ready.IsFaulted || Ready.IsCanceled) throw new InvalidOperationException("native_tool_host_not_ready");
            if (_calls.Values.Any(call => !call.Task.IsCompleted || !call.ReceiptConfirmed))
                throw new InvalidOperationException("native_tool_prior_outcome_unconfirmed");
            _deviceExecution = true;
            return GetDeclarations(_bindings.Values.ToArray()).EnumerateArray().Select(declaration =>
            {
                var name = declaration.GetProperty("name").GetString()!;
                var digest = WireJson.DomainDigest("tansr.sdk2.client-tool.v1", WireJson.EncodeControl(declaration));
                return new WindowsBusinessTool(name, digest, async (arguments, token) =>
                {
                    // Admission, authority, deadlines and exactly-once receipts belong to the
                    // original ExecutionHost. Only the same application delegate is adapted here.
                    lock (_gate) if (_stopping || !_deviceExecution) throw new InvalidOperationException("native_tool_host_stopping");
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                    try { return await InvokeBindingAsync(name, arguments, lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { return Receipt(error is InvalidOperationException ? error.Message : "native_tool_failed", true); }
                });
            }).ToArray();
        }
    }
#endif

    /// <summary>不得 await 工具执行来阻塞 SSE 读取；取消帧需继续抵达。</summary>
    internal void HandleEvent(AgentEvent item)
    {
        var type = Text(item.Data, "type") ?? item.Name;
        if (type != "server.tool.request" && type != "server.tool.cancel") return;
        var data = item.Data.TryGetProperty("payload", out var payload) ? payload : item.Data;
        var id = Text(data, "callId"); if (id == null) return;
        lock (_gate)
        {
            if (_stopping || _deviceExecution) return;
            if (type == "server.tool.cancel") { if (_calls.TryGetValue(id, out var cancelled)) cancelled.Stop.Cancel(); return; }
            if (_calls.TryGetValue(id, out var previous))
            {
                if (Text(data, "name") != Text(previous.Data, "name") || ArgumentText(data) != ArgumentText(previous.Data))
                { Report("tool_request_conflict"); return; }
                // 同一观察连接会丢弃重复 seq；重放仅重送已记录的原回执，不再调用委托。
                if (previous.Receipt.HasValue && previous.Task.IsCompleted) previous.Task = SubmitAsync(previous, previous.Receipt.Value, CancellationToken.None);
                return;
            }
            if (_calls.Count >= 256 && !RetireCompleted()) { Report("native_tool_host_capacity: pending_or_unconfirmed"); return; }
            var sequence = TrySequence(item.Id, out var parsed) ? (long?)parsed : null;
            var call = new Call(id, data.Clone(), sequence, sequence.HasValue && sequence.Value <= _retiredThrough); _calls[id] = call;
            call.Task = ExecuteAsync(call, Integer(item.Data, "ts"));
        }
    }

    private bool RetireCompleted()
    {
        var prior = _calls.Values.Where(x => x.Task.IsCompleted && x.ReceiptConfirmed && x.Sequence.HasValue).OrderBy(x => x.Sequence).FirstOrDefault();
        if (prior == null) return false;
        _calls.Remove(prior.Id); _retiredThrough = Math.Max(_retiredThrough, prior.Sequence!.Value); prior.Stop.Dispose(); return true;
    }

    private async Task ExecuteAsync(Call call, long? eventTime)
    {
        await Task.Yield();
        JsonElement receipt;
        var rejectedPrior = false;
        try
        {
            await Ready.ConfigureAwait(false);
            if (!call.Sequence.HasValue || call.Retired || _resumeBoundary.HasValue && call.Sequence.Value <= _resumeBoundary.Value)
            {
                rejectedPrior = true;
                throw new InvalidOperationException("native_tool_prior_outcome_unknown");
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var deadline = Integer(call.Data, "deadlineAt"); var ttl = Integer(call.Data, "ttlMs");
            // 示例没有可信时钟校准：超过5秒偏差保守拒绝，不把重放TTL重新变成执行许可。
            if (!eventTime.HasValue || Math.Abs((decimal)eventTime.Value - now) > 5000 ||
                !deadline.HasValue || !ttl.HasValue || ttl <= 0 || ttl > 60000 || deadline <= now)
                throw new InvalidOperationException("tool_clock_or_expiry_unconfirmed");
            call.Stop.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(ttl.Value, deadline.Value - now)));
            call.Stop.Token.ThrowIfCancellationRequested();
            call.Data.TryGetProperty("args", out var arguments);
            receipt = await InvokeBindingAsync(Text(call.Data, "name"), arguments, call.Stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { receipt = Receipt("native_tool_cancelled", true); }
        catch (Exception error) { receipt = Receipt(error is InvalidOperationException ? error.Message : "native_tool_failed", true); }
        lock (_gate) call.Receipt = receipt;
        await SubmitAsync(call, receipt, CancellationToken.None).ConfigureAwait(false);
        // Old unknown requests never become executable on replay. Do not permanently consume
        // the bounded ledger reserved for this host's new executions with historical rejections.
        if (rejectedPrior) lock (_gate) { _calls.Remove(call.Id); call.Stop.Dispose(); }
    }

    private async Task<JsonElement> InvokeBindingAsync(string? name, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (name != null && _bindings.TryGetValue(name, out var binding))
        {
            if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("invalid_tool_arguments");
            var receipt = await binding.ExecuteAsync(arguments, token).ConfigureAwait(false);
            if (receipt.ValueKind != JsonValueKind.Object || Text(receipt, "status") is not ("ok" or "error")) throw new InvalidOperationException("invalid_native_tool_receipt");
            return receipt.Clone();
        }
        if (name == "application_info")
            return Receipt(_application + "; version=" + typeof(NativeToolHost).Assembly.GetName().Version + "; runtime=" + Environment.Version + "; os=" + Environment.OSVersion.Platform, false);
        if (name != "set_window_title") throw new InvalidOperationException("unknown_native_tool");
        var title = Text(arguments, "title");
        if (string.IsNullOrWhiteSpace(title) || title!.Length > 120 || title.Any(char.IsControl)) throw new InvalidOperationException("invalid_window_title");
        await _setTitle(title, token).ConfigureAwait(false);
        return Receipt("window_title_updated", false);
    }

    private async Task SubmitAsync(Call call, JsonElement receipt, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _session.SubmitToolResultAsync(call.Id, receipt, deadline.Token).ConfigureAwait(false);
            if (!result.TryGetProperty("accepted", out var accepted) || accepted.ValueKind != JsonValueKind.True) throw new InvalidOperationException("invalid_tool_receipt_response");
            lock (_gate) call.ReceiptConfirmed = true;
        }
        catch (TansrHttpException error) when (error.DomainStatus == 409 && error.DomainCode == "call_already_resolved")
        { lock (_gate) call.ReceiptConfirmed = true; }
        catch (Exception error) { Report("tool_receipt_unconfirmed:" + (error is TansrException sdk ? sdk.Code : error.GetType().Name)); }
    }

    internal async Task DrainAsync(CancellationToken token)
    {
        Task[] tasks;
        lock (_gate) { _stopping = true; _lifetime.Cancel(); foreach (var call in _calls.Values) call.Stop.Cancel(); tasks = _calls.Values.Select(x => x.Task).Append(Ready).ToArray(); }
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
            _lifetime.Cancel();
            foreach (var call in _calls.Values) { call.Stop.Cancel(); if (call.Task.IsCompleted) call.Stop.Dispose(); }
        }
    }
    private void Report(string code) { try { _report(code); } catch { /* 宿主日志故障不得重跑工具。 */ } }
    private static bool TrySequence(string? value, out long sequence)
    {
        sequence = 0;
        return value != null && value.Length > 0 && (value.Length == 1 || value[0] != '0') &&
            value.All(ch => ch >= '0' && ch <= '9') && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence <= 9007199254740991L;
    }
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
    private sealed class Call(string id, JsonElement data, long? sequence, bool retired)
    {
        internal readonly string Id = id; internal readonly JsonElement Data = data; internal readonly long? Sequence = sequence; internal readonly bool Retired = retired;
        internal readonly CancellationTokenSource Stop = new(); internal Task Task = System.Threading.Tasks.Task.CompletedTask; internal JsonElement? Receipt; internal bool ReceiptConfirmed;
    }
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
