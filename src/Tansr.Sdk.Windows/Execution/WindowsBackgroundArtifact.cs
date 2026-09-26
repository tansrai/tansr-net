using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Tansr.Sdk.Windows.Execution;

// 本实例持有原生文件句柄。所有名字由宿主生成，不接受模型路径；工件不是执行耐久见证。
internal sealed class WindowsBackgroundArtifact
{
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly SafeFileHandle handle;
    private readonly FileStream stream;
    private readonly int limit;
    private readonly Queue<byte[]> pending = new();
    private readonly Decoder stdout = new UTF8Encoding(false, true).GetDecoder(), stderr = new UTF8Encoding(false, true).GetDecoder();
    private Task work = Task.CompletedTask;
    private long captured, written;
    private int pendingBytes;
    private bool pumping, truncated, captureStopped, failed, ended, sealedOutput, deleted;
    private Task? deleting;

    internal WindowsBackgroundArtifact(SafeFileHandle parent, string name, int maximumBytes)
    {
        limit = maximumBytes;
        handle = NativeWorkspace.OpenFile(parent, name, write: true, create: true);
        try { stream = new FileStream(handle, FileAccess.ReadWrite, 16384, false); }
        catch { try { NativeWorkspace.DeleteOpenedFile(handle); } finally { handle.Dispose(); } throw; }
    }

    internal long Bytes { get { lock (gate) return written; } }
    internal bool Truncated { get { lock (gate) return truncated || failed; } }
    internal bool Deleted { get { lock (gate) return deleted; } }
    internal void Truncate() { lock (gate) truncated = true; }

    internal void Capture(WindowsProcessOutputChunk chunk)
    {
        lock (gate)
        {
            if (ended || deleted || failed || captureStopped) { if (chunk.ByteCount != 0) truncated = true; return; }
            try
            {
                var raw = chunk.RawBytes.ToArray(); var chars = new char[Encoding.UTF8.GetMaxCharCount(raw.Length)];
                var decoder = chunk.Stream == WindowsProcessOutputStream.StandardOutput ? stdout : stderr;
                int count = decoder.GetChars(raw, 0, raw.Length, chars, 0, false);
                Enqueue(Encoding.UTF8.GetBytes(chars, 0, count));
            }
            catch (DecoderFallbackException) { truncated = captureStopped = true; }
        }
    }

    private void Enqueue(byte[] bytes)
    {
        for (int offset = 0; offset < bytes.Length; offset += 16384)
        {
            int size = Math.Min(16384, bytes.Length - offset);
            if (captured + size > limit || pendingBytes + size > 2097152) { truncated = captureStopped = true; break; }
            var copy = new byte[size]; Buffer.BlockCopy(bytes, offset, copy, 0, size);
            captured += size; pendingBytes += size; pending.Enqueue(copy);
        }
        if (!pumping && pending.Count != 0) { pumping = true; work = Task.Run(PumpAsync); }
    }

    private async Task PumpAsync()
    {
        for (; ; )
        {
            byte[] bytes;
            lock (gate)
            {
                if (pending.Count == 0) { pumping = false; return; }
                bytes = pending.Peek();
            }
            await io.WaitAsync().ConfigureAwait(false);
            try
            {
                long position; lock (gate) position = written;
                if (NativeWorkspace.Validate(handle, false).Length != position) throw new IOException("工件身份或长度变化。");
                stream.Position = position;
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                // FileStream的写缓存须提交，后续句柄长度核对才对应已写前缀。
                await stream.FlushAsync().ConfigureAwait(false);
                lock (gate) { written += bytes.Length; pending.Dequeue(); pendingBytes -= bytes.Length; }
            }
            catch
            {
                lock (gate) { failed = truncated = true; pending.Clear(); pendingBytes = 0; pumping = false; }
                return;
            }
            finally { Array.Clear(bytes, 0, bytes.Length); io.Release(); }
        }
    }

    internal async Task FinishAsync()
    {
        Task writing;
        lock (gate)
        {
            if (!ended)
            {
                if (!truncated && !failed)
                {
                    try
                    {
                        foreach (var decoder in new[] { stdout, stderr })
                        { var chars = new char[8]; int count = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true); Enqueue(Encoding.UTF8.GetBytes(chars, 0, count)); }
                    }
                    catch (DecoderFallbackException) { truncated = true; }
                }
                ended = true;
            }
            writing = work;
        }
        await writing.ConfigureAwait(false);
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) { if (failed || deleted) return; }
            NativeWorkspace.Validate(handle, false); stream.Flush(true);
            lock (gate) sealedOutput = true;
        }
        catch { lock (gate) failed = truncated = true; }
        finally { io.Release(); }
    }

    internal async Task WriteReadAsync(Utf8JsonWriter writer, long offset, int length)
    {
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            long total; bool gap, complete;
            lock (gate) { total = written; gap = deleted || failed || offset > total || truncated && offset >= total; complete = !truncated && sealedOutput; }
            byte[] bytes = Array.Empty<byte>();
            if (!gap)
            {
                if (NativeWorkspace.Validate(handle, false).Length != total) throw new IOException("工件长度变化。");
                bytes = new byte[(int)Math.Min(length, total - offset)]; stream.Position = offset;
                int at = 0; while (at < bytes.Length) { int count = await stream.ReadAsync(bytes, at, bytes.Length - at).ConfigureAwait(false); if (count == 0) throw new IOException("工件短读。"); at += count; }
            }
            writer.WriteString("offset", offset.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("nextOffset", (offset + bytes.Length).ToString(CultureInfo.InvariantCulture));
            writer.WriteString("base64", Convert.ToBase64String(bytes)); writer.WriteNumber("byteLength", bytes.Length);
            using var hash = SHA256.Create(); writer.WriteString("payloadDigest", BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
            writer.WriteBoolean("complete", !gap && complete && offset + bytes.Length == total); writer.WriteBoolean("gap", gap);
        }
        finally { io.Release(); }
    }

    internal Task DeleteAsync() { lock (gate) return deleting ??= DeleteCoreAsync(); }
    private async Task DeleteCoreAsync()
    {
        await FinishAsync().ConfigureAwait(false);
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (deleted) return;
            NativeWorkspace.Validate(handle, false);
            NativeWorkspace.DeleteOpenedFile(handle); // 删除固定对象，不在关闭后按路径寻找替代物。
            lock (gate) deleted = true;
        }
        catch { lock (gate) failed = truncated = true; throw; }
        finally { try { stream.Dispose(); } finally { handle.Dispose(); io.Release(); } }
    }
}
