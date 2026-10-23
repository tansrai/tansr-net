using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// Presentation only: interrupt ACK does not prove that a turn was cancelled.
// One instance belongs to one connection; it never sends, retries or stops observation.
internal sealed class ExampleCancellationFeedback
{
    private readonly object _gate = new();
    private readonly string _sessionId;
    private long _lastSequence = -1, _attempt;
    private string? _currentTurn, _requestedTurn, _terminal, _requestFailure, _observationFailure;
    private bool _visible, _accepted, _detached;

    internal ExampleCancellationFeedback(string sessionId) => _sessionId = sessionId;

    internal string? Text
    {
        get
        {
            lock (_gate)
            {
                if (!_visible || _detached) return null;
                var text = _terminal ?? (_requestFailure != null || _observationFailure != null
                    ? "原轮取消结果未知；请查状态或历史，不要自动重发。"
                    : _requestedTurn == null
                        ? "当前轮次尚未确认；取消结果须查状态或历史。"
                        : _accepted ? "取消请求已受理，等待原轮运行终态。" : "正在请求取消当前工作；等待服务端回执。");
                if (_requestFailure != null) text += " 取消请求未确认：" + _requestFailure;
                if (_observationFailure != null) text += " 事件观察未确认：" + _observationFailure;
                return text;
            }
        }
    }

    internal long Begin()
    {
        lock (_gate)
        {
            if (_detached) throw new InvalidOperationException("cancellation_presentation_detached");
            _attempt++; _visible = true; _accepted = false; _requestedTurn = _currentTurn;
            _terminal = null; _requestFailure = null;
            return _attempt;
        }
    }

    internal void Acknowledge(long attempt)
    {
        lock (_gate) if (!_detached && _visible && attempt == _attempt) _accepted = true;
    }

    internal void Fail(long attempt, string safeError)
    {
        lock (_gate) if (!_detached && _visible && attempt == _attempt) _requestFailure = safeError;
    }

    internal bool ObservationFailed(string safeError)
    {
        lock (_gate)
        {
            if (_detached) return false;
            _observationFailure = safeError;
            return _visible;
        }
    }

    internal bool ObservationEnded()
    {
        lock (_gate) return _terminal == null && ObservationFailed("会话观察已结束，未取得原轮终态。");
    }

    internal void Dismiss()
    {
        lock (_gate) { _visible = false; _attempt++; }
    }

    internal void Detach()
    {
        lock (_gate) { _detached = true; _visible = false; _attempt++; }
    }

    internal bool Observe(AgentEvent item)
    {
        lock (_gate)
        {
            var data = item.Data;
            if (_detached || Get(data, "sessionId") != _sessionId) return false;
            var type = Get(data, "type") ?? item.Name;
            if (type == "server.replay.gap") return ObservationFailed("事件存在缺口，请核对原轮终态。");
            if (!data.TryGetProperty("seq", out var seq) || !seq.TryGetInt64(out var sequence) || sequence <= _lastSequence) return false;
            _lastSequence = sequence;
            var turn = Get(data, "turnId");
            if (type == "turn.started")
            {
                _currentTurn = turn;
                if (_visible && _terminal == null && turn != _requestedTurn)
                    return ObservationFailed("已观察到另一轮，原轮终态尚未确认。");
            }
            else if (type == "session.created" || type == "session.ended")
            {
                _currentTurn = null;
                if (_visible && _terminal == null)
                    return ObservationFailed("会话生命周期已变化，原轮终态尚未确认。");
            }
            else if (type == "turn.completed" || type == "turn.aborted")
            {
                if (turn != null && turn == _currentTurn) _currentTurn = null;
                if (!_visible || turn == null || turn != _requestedTurn) return false;
                var reason = Get(data, "reason");
                _terminal = type == "turn.aborted" && (reason == "aborted_streaming" || reason == "aborted_tools")
                    ? "原轮已取消（服务端已确认中止）。"
                    : type == "turn.completed" && (reason == "completed" || reason == "structured_output")
                        ? "原轮已完成；取消请求不代表取消成功。"
                        : "原轮已结束；未确认取消成功。";
                return true;
            }
            return false;
        }
    }

    private static string? Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
