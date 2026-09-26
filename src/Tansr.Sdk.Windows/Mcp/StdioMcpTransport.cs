using System.Collections.Concurrent;
using System.Text.Json;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Mcp;

internal sealed class StdioMcpTransport : IMcpTransport
{
    private readonly WindowsDuplexProcess _process;
    private readonly int _maximumBytes;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private int _closed;
    internal StdioMcpTransport(WindowsDuplexProcess process, int maximumBytes)
    { _process = process; _maximumBytes = maximumBytes; _pump = PumpAsync(); }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public string? ProtocolVersion { get; set; }

    public async Task<JsonElement> RequestAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (IsClosed) throw new McpException("transport_closed");
        string id = McpJson.Id(message);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new McpException("duplicate_request");
        using var canceled = cancellationToken.Register(() => completion.TrySetCanceled());
        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            return McpJson.Result(await completion.Task.ConfigureAwait(false));
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task SendAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (IsClosed) throw new McpException("transport_closed");
        return _process.WriteLineAsync(message.GetRawText(), cancellationToken);
    }

    private async Task PumpAsync()
    {
        Exception failure = new McpException("transport_closed");
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                string? line = await _process.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line == null) break;
                foreach (var message in McpJson.Frames(McpJson.Parse(line, _maximumBytes)))
                {
                    if (message.TryGetProperty("method", out _))
                    {
                        if (message.TryGetProperty("id", out _)) await SendAsync(McpJson.ServerReply(message), _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (_pending.TryGetValue(McpJson.Id(message), out var request)) request.TrySetResult(message.Clone());
                    // 过期请求的迟到回执不触发重发，也不被当成另一请求的结果。
                }
            }
        }
        catch (Exception error) { failure = error is McpException ? error : new McpException("transport_failed"); }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            foreach (var pending in _pending.Values) pending.TrySetException(failure);
            await _process.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task CloseAsync()
    {
        Interlocked.Exchange(ref _closed, 1); _stop.Cancel();
        var result = await _process.CloseAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        if (!result.CleanupConfirmed || !result.IoSettled) throw new McpException("cleanup_incomplete");
    }
}
