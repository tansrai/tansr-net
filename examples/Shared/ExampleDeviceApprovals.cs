namespace Tansr.Examples;

// 控制台的本地批准队列，永不与主命令循环争读 stdin。取消关闭原请求，迟到回答不再授权。
internal sealed class ExampleDeviceApprovals : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> pending = new(StringComparer.Ordinal);
    private readonly Action<string> write;
    private bool disposed;
    internal ExampleDeviceApprovals(Action<string> write) => this.write = write;
    internal async Task<bool> RequestAsync(string description, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N"); var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(ExampleDeviceApprovals)); pending.Add(id, response); }
        using var registration = ct.Register(() => response.TrySetCanceled());
        try { write("device_approval=" + id + "\n" + description + "\n/device-allow " + id + " 或 /device-deny " + id); return await response.Task.ConfigureAwait(false); }
        finally { lock (gate) pending.Remove(id); write("device_approval_closed=" + id); }
    }
    internal bool Answer(string id, bool allow)
    { lock (gate) return !disposed && pending.TryGetValue(id, out var response) && response.TrySetResult(allow); }
    public void Dispose()
    { lock (gate) { disposed = true; foreach (var response in pending.Values) response.TrySetCanceled(); pending.Clear(); } }
}
