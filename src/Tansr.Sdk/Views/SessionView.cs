using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Views;

/// <summary>
/// 将一条会话事件流投影为有界不可变快照；不发起网络请求，不执行工具，不改变会话事实。
/// 一个 ObserveAsync 泵调用 Apply，多个订阅者共享快照，不争抢事件流。
/// </summary>
public sealed class SessionView : IDisposable
{
    private const long MaximumEventSequence = 9007199254740991L;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly List<Message> _messages = new();
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);
    private readonly List<SessionNoticeView> _notices = new();
    private readonly Dictionary<string, SessionRequestView> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentProgressView> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Block> _blocks = new();
    private readonly SessionViewOptions _options;
    private List<TodoView> _todos = new();
    private Message? _current;
    private SessionNoticeView? _error;
    private SessionViewSnapshot _snapshot;
    private long _structuredBytes, _turn, _nextTool, _version, _nextMessage, _turnTokens, _sessionTokens, _usageRequests;
    private long? _lastSequence;
    private string? _sessionId;
    private bool _running, _failed, _compacting, _sealed, _gap, _truncated, _disposed;

    public SessionView(SessionViewOptions? options = null)
    {
        options ??= new SessionViewOptions();
        if (options.MaximumMessages < 1 || options.MaximumMessages > 4096 ||
            options.MaximumPartsPerMessage < 1 || options.MaximumPartsPerMessage > 4096 ||
            options.MaximumTextCharacters < 1 || options.MaximumTextCharacters > 1048576 ||
            options.MaximumOutputCharacters < 1 || options.MaximumOutputCharacters > 1048576 ||
            options.InitialSequence < -1 || options.InitialSequence > MaximumEventSequence || !Enum.IsDefined(typeof(TextDeliveryMode), options.TextDelivery) ||
            !Enum.IsDefined(typeof(ThinkingDeliveryMode), options.ThinkingDelivery))
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = new SessionViewOptions
        {
            TextDelivery = options.TextDelivery,
            ThinkingDelivery = options.ThinkingDelivery,
            MaximumMessages = options.MaximumMessages,
            MaximumPartsPerMessage = options.MaximumPartsPerMessage,
            MaximumTextCharacters = options.MaximumTextCharacters,
            MaximumOutputCharacters = options.MaximumOutputCharacters,
            InitialSequence = options.InitialSequence,
        };
        _lastSequence = options.InitialSequence;
        _snapshot = BuildSnapshot();
    }

    public SessionViewSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    /// <summary>UI 可传同步上下文。慢订阅者只保留最新完整快照；异常不打断其它订阅或事件泵。</summary>
    public IDisposable Subscribe(Action<SessionViewSnapshot> observer, SynchronizationContext? context = null,
        Action<Exception>? onObserverError = null)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        Subscription subscription;
        SessionViewSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            subscription = new Subscription(this, observer, context, onObserverError);
            _subscriptions.Add(subscription); snapshot = _snapshot;
        }
        subscription.Publish(snapshot);
        return subscription;
    }

    /// <summary>宿主只在输入已接纳后显式加入用户消息；网络未知时应保留草稿并先对账。</summary>
    public void AppendUserMessage(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        Update(() =>
        {
            var message = AddMessage("user");
            message.Parts.Add(new Part("text") { Text = Prefix(text), Truncated = text.Length > _options.MaximumTextCharacters });
            return true;
        });
    }

    public void Apply(AgentEvent item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        Update(() =>
        {
            var data = item.Data;
            var type = String(data, "type") ?? item.Name;
            var sessionId = String(data, "sessionId");
            if (sessionId != null)
            {
                if (_sessionId != null && _sessionId != sessionId)
                    throw new InvalidOperationException("A SessionView cannot consume events from different sessions.");
                _sessionId = sessionId;
            }
            if (type == "server.replay.gap")
            {
                _gap = true;
                Notice(type, String(data, "reason"), false, data.Clone());
                // 新日志纪元必须重建，旧快照不与新序号拼接。
                if (String(data, "reason") == "ahead_of_log") _epochLost = true;
                return true;
            }
            if (_epochLost) return false;
            long? sequence = Integer(data, "seq");
            if (!sequence.HasValue && long.TryParse(item.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0)
                sequence = id;
            if (sequence.HasValue)
            {
                if (sequence < 0 || sequence > MaximumEventSequence) throw new ArgumentException("Event sequence must be a nonnegative safe integer.", nameof(item));
                if (_lastSequence.HasValue && sequence.Value <= _lastSequence.Value) return false;
                if (_lastSequence.HasValue && _lastSequence.Value != long.MaxValue && sequence.Value > _lastSequence.Value + 1)
                {
                    _gap = true;
                    Notice("event_sequence_gap", (_lastSequence.Value + 1).ToString(CultureInfo.InvariantCulture) + ".." + (sequence.Value - 1).ToString(CultureInfo.InvariantCulture));
                }
                _lastSequence = sequence;
            }
            var payload = Property(data, "payload");
            Reduce(type, type.StartsWith("server.", StringComparison.Ordinal) && payload?.ValueKind == JsonValueKind.Object ? payload.Value : data);
            return true;
        });
    }

    private bool _epochLost;

    /// <summary>事件源故障独立于 turn.error；不会伪造模型或工具已完成。</summary>
    public void MarkSourceFailure(string code, string message)
    {
        Update(() => { _error = new SessionNoticeView(code, Prefix(message), true); return true; });
    }

    private void Update(Func<bool> action)
    {
        Subscription[] subscribers;
        SessionViewSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!action()) return;
            _version++; snapshot = _snapshot = BuildSnapshot(); subscribers = _subscriptions.ToArray();
        }
        foreach (var subscription in subscribers) subscription.Publish(snapshot);
    }

    private void Reduce(string type, JsonElement data)
    {
        if (_compacting && type != "session.compacted" && type != "session.microcompacted") _compacting = false;
        switch (type)
        {
            case "turn.started":
                _turn++; _running = true; _failed = false; _error = null; _turnTokens = 0; _current = null; _blocks.Clear(); _sealed = false;
                break;
            case "turn.completed":
            case "turn.aborted":
            case "session.ended":
                _running = false; _failed = String(data, "reason") == "model_error" || String(data, "reason") == "internal_error";
                foreach (var tool in _tools.Values.Where(t => IsActive(t.Status))) tool.Status = "aborted";
                _current = null; _blocks.Clear(); _requests.Clear();
                break;
            case "turn.error":
                var notice = new SessionNoticeView(String(data, "errorKind") ?? String(data, "scope") ?? "turn.error",
                    Prefix(String(data, "message") ?? ""), !Boolean(data, "recoverable"), Property(data, "detail"));
                if (notice.IsError) _error = notice; else AddNotice(notice);
                break;
            case "server.platform.warning":
                Notice(String(data, "code") ?? type, String(data, "message")); break;
            case "session.events_dropped":
                _gap = true; Notice(type, String(data, "reason"), false, data.Clone()); break;
            case "session.compacted":
            case "session.microcompacted": _compacting = true; break;
            case "msg.block.start": StartBlock(data); break;
            case "msg.text.delta": Delta(data, "text"); break;
            case "msg.thinking.delta": Delta(data, "thinking"); break;
            case "msg.block.end": EndBlock(data, false); break;
            case "msg.retracted": EndBlock(data, true); break;
            case "tool.proposed":
            case "tool.permission.requested":
            case "tool.permission.decided":
            case "tool.started":
            case "tool.progress":
            case "tool.output.delta":
            case "tool.completed":
            case "tool.failed": ReduceTool(type, data); break;
            case "cost.usage.updated":
                var usage = Property(data, "usage");
                if (usage.HasValue)
                {
                    var amount = 0L;
                    foreach (var key in new[] { "inputTokens", "outputTokens", "cacheReadInputTokens", "cacheCreationInputTokens" })
                        amount = AddCount(amount, Math.Max(0, Integer(usage.Value, key) ?? 0));
                    _turnTokens = AddCount(_turnTokens, amount); _sessionTokens = AddCount(_sessionTokens, amount); _usageRequests = AddCount(_usageRequests, 1);
                }
                if (String(data, "purpose") == null && String(data, "agentId") == null) _sealed = true;
                break;
            case "plan.todo.updated":
                var items = Property(data, "items");
                if (items?.ValueKind == JsonValueKind.Array)
                {
                    _truncated |= items.Value.GetArrayLength() > 256;
                    _todos = items.Value.EnumerateArray().Take(256).Select(x => new TodoView(String(x, "id") ?? "", Prefix(String(x, "content") ?? ""), String(x, "status") ?? "pending")).ToList();
                }
                break;
            case "server.permission.request":
            case "server.question.request":
                var requestId = String(data, "requestId");
                if (requestId != null)
                {
                    if (_requests.Count >= 64 && !_requests.ContainsKey(requestId))
                    { _gap = true; _truncated = true; Notice("request_capacity", null); break; }
                    _requests[requestId] = new SessionRequestView(type == "server.permission.request" ? "permission" : "question", requestId, data.Clone());
                }
                break;
            case "server.permission.closed":
            case "server.question.closed":
                var closed = String(data, "requestId"); if (closed != null) _requests.Remove(closed); break;
            default:
                if (type.StartsWith("agent.", StringComparison.Ordinal)) ReduceAgent(type, data);
                break;
        }
    }

    private void StartBlock(JsonElement data)
    {
        var index = BlockIndex(data); if (!index.HasValue) return;
        if (_current == null || _sealed || _blocks.ContainsKey(index.Value))
        { _current = AddMessage("assistant"); _blocks.Clear(); _sealed = false; }
        if (_blocks.Count >= _options.MaximumPartsPerMessage) { _truncated = true; return; }
        var kind = String(data, "blockType") ?? "unknown";
        var block = new Block(kind);
        if (kind == "text" || kind == "thinking")
        {
            block.Hidden = kind == "thinking" && _options.ThinkingDelivery == ThinkingDeliveryMode.Off;
            block.Final = kind == "text" ? _options.TextDelivery == TextDeliveryMode.Final : _options.ThinkingDelivery == ThinkingDeliveryMode.Final;
            if (!block.Hidden && !block.Final) { block.Part = new Part(kind); AddPart(_current, block.Part); }
        }
        _blocks[index.Value] = block;
    }

    private void Delta(JsonElement data, string kind)
    {
        var index = BlockIndex(data);
        if (!index.HasValue || !_blocks.TryGetValue(index.Value, out var block) || !block.Open || block.Kind != kind)
        { _gap = true; Notice("orphan_delta", kind); return; }
        if (block.Hidden) return;
        var text = String(data, "text") ?? "";
        if (block.Final)
        { block.Truncated |= block.Buffer.Length + (long)text.Length > _options.MaximumTextCharacters; block.Buffer = Prefix(block.Buffer + text); }
        else if (block.Part != null)
        { block.Part.Truncated |= block.Part.Text.Length + (long)text.Length > _options.MaximumTextCharacters; block.Part.Text = Prefix(block.Part.Text + text); }
    }

    private void EndBlock(JsonElement data, bool retract)
    {
        var index = BlockIndex(data);
        if (!index.HasValue || !_blocks.TryGetValue(index.Value, out var block)) return;
        if (retract)
        { if (block.Part != null) _current?.Parts.Remove(block.Part); block.Buffer = ""; block.Open = false; return; }
        if (!block.Open) return;
        block.Open = false;
        if (!block.Hidden && block.Final && _current != null)
        { block.Part = new Part(block.Kind) { Text = block.Buffer, Truncated = block.Truncated }; AddPart(_current, block.Part); block.Buffer = ""; }
    }

    private void ReduceTool(string type, JsonElement data)
    {
        var id = String(data, "toolCallId"); if (id == null) return;
        var key = _turn.ToString(CultureInfo.InvariantCulture) + ":" + id;
        if (!_tools.TryGetValue(key, out var tool))
        {
            if (type != "tool.proposed" && type != "tool.started" && type != "tool.permission.requested") return;
            if (_tools.Count >= 1024) { _truncated = true; return; }
            tool = new Tool(id, "tool-" + (++_nextTool).ToString(CultureInfo.InvariantCulture), String(data, "name") ?? id); _tools[key] = tool;
            _current ??= AddMessage("assistant"); AddPart(_current, new Part("toolCall") { ToolId = id, ToolInstanceId = tool.InstanceId });
        }
        switch (type)
        {
            case "tool.permission.requested": tool.Status = "awaiting_permission"; break;
            case "tool.permission.decided": tool.Status = String(data, "decision") == "deny" ? "denied" : "proposed"; break;
            case "tool.started": tool.Status = "running"; _sealed = true; break;
            case "tool.progress": tool.Progress = Prefix(String(data, "message") ?? ""); break;
            case "tool.output.delta":
                var joined = tool.Output + (String(data, "chunk") ?? "");
                tool.Truncated |= joined.Length > _options.MaximumOutputCharacters;
                _truncated |= tool.Truncated;
                tool.Output = Tail(joined, _options.MaximumOutputCharacters); break;
            case "tool.completed":
                tool.Status = Boolean(data, "isError") ? "failed" : "completed";
                SetToolResult(tool, data); tool.ResultText = Prefix(String(data, "content") ?? ""); break;
            case "tool.failed": tool.Status = "failed"; tool.Error = Prefix(String(data, "message") ?? String(data, "errorType") ?? ""); break;
        }
    }

    private void SetToolResult(Tool tool, JsonElement data)
    {
        _structuredBytes -= tool.ResultBytes; tool.Result = null; tool.ResultBytes = 0;
        if (!data.TryGetProperty("data", out var result)) return;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(result.GetRawText());
        // 与历史单产物的 8 MiB 窗口一致；视图总结构化呈现上限 32 MiB，权威结果仍在服务历史。
        if (bytes > 8 * 1024 * 1024) { _truncated = true; return; }
        foreach (var previous in _tools.Values)
        {
            if (_structuredBytes + bytes <= 32 * 1024 * 1024) break;
            if (previous.ResultBytes == 0) continue;
            _structuredBytes -= previous.ResultBytes; previous.Result = null; previous.ResultBytes = 0; _truncated = true;
        }
        tool.Result = result.Clone(); tool.ResultBytes = bytes; _structuredBytes += bytes;
    }

    private void ReduceAgent(string type, JsonElement data)
    {
        var id = String(data, "agentId"); if (id == null) return;
        if (_agents.Count >= 256 && !_agents.ContainsKey(id)) { _truncated = true; return; }
        _agents.TryGetValue(id, out var previous);
        var status = type == "agent.completed" ? "completed" : type == "agent.settled" ? "settled" : type == "agent.interrupted" ? "interrupted" : "running";
        _agents[id] = new AgentProgressView(id, status, Prefix(String(data, "message") ?? String(data, "outcome") ?? previous?.Message ?? ""));
    }

    private Message AddMessage(string role)
    {
        if (_messages.Count >= _options.MaximumMessages)
        {
            var removed = _messages[0]; _messages.RemoveAt(0); _truncated = true;
            foreach (var part in removed.Parts)
                foreach (var key in _tools.Where(x => x.Value.InstanceId == part.ToolInstanceId).Select(x => x.Key).ToArray()) { _structuredBytes -= _tools[key].ResultBytes; _tools.Remove(key); }
        }
        var message = new Message("msg-" + (++_nextMessage).ToString(CultureInfo.InvariantCulture), role);
        _messages.Add(message); return message;
    }

    private void AddPart(Message message, Part part)
    { if (message.Parts.Count < _options.MaximumPartsPerMessage) message.Parts.Add(part); else _truncated = true; }

    private SessionViewSnapshot BuildSnapshot()
    {
        var status = _failed ? "error" : !_running ? "idle" : _compacting ? "compacting" :
            _requests.Values.Any(x => x.Kind == "permission") || _tools.Values.Any(x => x.Status == "awaiting_permission") ? "awaiting_permission" :
            _requests.Values.Any(x => x.Kind == "question") ? "awaiting_question" :
            _tools.Values.Any(x => IsActive(x.Status)) ? "tooling" :
            _blocks.Values.Any(x => x.Open && x.Kind == "text") ? "responding" : "thinking";
        return new SessionViewSnapshot(_version, _lastSequence, status,
            _messages.Select(m => new MessageView(m.Id, m.Role, m.Parts.Select(p => new MessagePartView(p.Kind, p.Text, p.ToolId, p.ToolInstanceId, p.Truncated)).ToList())).ToList(),
            _tools.Values.Select(t => new ToolView(t.Id, t.InstanceId, t.Name, t.Status, t.Output, t.Progress, t.ResultText, t.Result, t.Error, t.Truncated)).ToList(),
            _notices.ToList(), _requests.Values.ToList(), _todos.ToList(), _agents.Values.ToList(),
            _turnTokens, _sessionTokens, _usageRequests, _gap, _truncated, _error);
    }

    private void Notice(string code, string? message, bool error = false, JsonElement? detail = null)
        => AddNotice(new SessionNoticeView(code, message == null ? null : Prefix(message), error, detail));
    private void AddNotice(SessionNoticeView notice) { if (_notices.Count == 20) { _notices.RemoveAt(0); _truncated = true; } _notices.Add(notice); }
    private string Prefix(string value)
    {
        var length = Math.Min(value.Length, _options.MaximumTextCharacters);
        if (length < value.Length) _truncated = true;
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value.Substring(0, length);
    }
    private static string Tail(string value, int maximum)
    {
        var start = Math.Max(0, value.Length - maximum);
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start])) start++;
        return value.Substring(start);
    }
    private static bool IsActive(string status) => status == "proposed" || status == "running" || status == "awaiting_permission";
    private static long AddCount(long current, long added) => current > long.MaxValue - added ? long.MaxValue : current + added;
    private static int? BlockIndex(JsonElement data) { var value = Integer(data, "index"); return value >= 0 && value <= int.MaxValue ? (int?)value : null; }
    private static JsonElement? Property(JsonElement data, string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) ? value.Clone() : null;
    private static string? String(JsonElement data, string key) { var value = Property(data, key); return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null; }
    private static long? Integer(JsonElement data, string key) { var value = Property(data, key); return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number) ? number : null; }
    private static bool Boolean(JsonElement data, string key) => Property(data, key)?.ValueKind == JsonValueKind.True;
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SessionView)); }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate) { if (_disposed) return; _disposed = true; subscriptions = _subscriptions.ToArray(); _subscriptions.Clear(); }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    private sealed class Message(string id, string role)
    { internal readonly string Id = id, Role = role; internal readonly List<Part> Parts = new(); }
    private sealed class Part(string kind)
    { internal readonly string Kind = kind; internal string Text = ""; internal string? ToolId, ToolInstanceId; internal bool Truncated; }
    private sealed class Block(string kind)
    { internal readonly string Kind = kind; internal bool Open = true, Hidden, Final, Truncated; internal Part? Part; internal string Buffer = ""; }
    private sealed class Tool(string id, string instanceId, string name)
    { internal readonly string Id = id, InstanceId = instanceId, Name = name; internal string Status = "proposed", Output = ""; internal string? Progress, ResultText, Error; internal JsonElement? Result; internal int ResultBytes; internal bool Truncated; }

    private sealed class Subscription(SessionView owner, Action<SessionViewSnapshot> observer,
        SynchronizationContext? context, Action<Exception>? onError) : IDisposable
    {
        private readonly object _lock = new();
        private SessionViewSnapshot? _pending;
        private long _latestVersion = -1;
        private bool _scheduled, _stopped;

        internal void Publish(SessionViewSnapshot snapshot)
        {
            lock (_lock)
            {
                if (_stopped || snapshot.Version <= _latestVersion) return;
                _latestVersion = snapshot.Version; _pending = snapshot;
                if (_scheduled) return; _scheduled = true;
            }
            try { if (context == null) Drain(); else context.Post(_ => Drain(), null); }
            catch (Exception error) { Dispose(); Report(error); }
        }
        private void Drain()
        {
            while (true)
            {
                SessionViewSnapshot? next;
                lock (_lock)
                {
                    if (_stopped || _pending == null) { _scheduled = false; return; }
                    next = _pending; _pending = null;
                }
                try { observer(next); } catch (Exception error) { Report(error); }
            }
        }
        private void Report(Exception error) { try { onError?.Invoke(error); } catch { /* 诊断回调不能破坏其它订阅。 */ } }
        public void Dispose()
        {
            lock (_lock) { _stopped = true; _pending = null; }
            lock (owner._gate) owner._subscriptions.Remove(this);
        }
    }
}
