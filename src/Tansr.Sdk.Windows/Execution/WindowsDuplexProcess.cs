using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>
/// 持久原生 stdio 通道。仅负责 UTF-8 行帧及资源生命周期，JSON-RPC/MCP 状态由上层维护。
/// 调用方必须先批准程序、参数、环境与目录；Job Object 回收不是文件/网络沙箱。
/// </summary>
public sealed class WindowsDuplexProcess : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly object gate = new();
    private readonly Settings settings;
    private readonly WindowsDuplexProcessNative native;
    private readonly IWindowsProcessWorkingDirectoryLease directory;
    private readonly Queue<string> lines = new();
    private readonly SemaphoreSlim lineSignal = new(0);
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly SemaphoreSlim pendingWrites;
    private readonly Task outputPump;
    private readonly Task errorPump;
    private Task activeWrite = Task.CompletedTask;
    private CancellationTokenRegistration lifetimeRegistration;
    private WindowsDuplexProcessTermination? requestedStop;
    private bool readsComplete;
    private int reading;
    private long stderrBytes;
    private long stdoutBytes;

    private WindowsDuplexProcess(Settings settings, WindowsDuplexProcessNative native,
        IWindowsProcessWorkingDirectoryLease directory, CancellationToken lifetimeCancellation)
    {
        this.settings = settings;
        this.native = native;
        this.directory = directory;
        pendingWrites = new SemaphoreSlim(settings.MaxPendingWrites, settings.MaxPendingWrites);
        // 同步匿名管道使用专用线程，不占用 UI 线程，也不以 ThreadPool 饥饿阻碍关闭逻辑。
        outputPump = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        errorPump = Task.Factory.StartNew(ReadError, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        lifetimeRegistration = lifetimeCancellation.Register(() => RequestStop(WindowsDuplexProcessTermination.Canceled));
        Completion = MonitorAsync();
    }

    public int ProcessId => native.ProcessId;
    public Task<WindowsDuplexProcessExit> Completion { get; }

    internal NativeProcessHandle DuplicateProcessHandle()
    {
        lock (gate)
        {
            if (requestedStop.HasValue || Completion.IsCompleted) throw new WindowsDuplexProcessException("closed");
            return native.DuplicateProcessHandle();
        }
    }

    public static async Task<WindowsDuplexProcess> StartAsync(WindowsDuplexProcessOptions options, CancellationToken lifetimeCancellation = default)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("This transport requires Windows.");
        var settings = new Settings(options ?? throw new ArgumentNullException(nameof(options)));
        return await Task.Run(() =>
        {
            lifetimeCancellation.ThrowIfCancellationRequested();
            var lease = options.AcquireWorkingDirectory() ?? throw new InvalidOperationException("A working directory lease is required.");
            WindowsDuplexProcessNative? process = null;
            try
            {
                lease.ValidateForExecution();
                WindowsDuplexProcessNative.ValidatePath(lease.DirectoryPath);
                process = WindowsDuplexProcessNative.Start(settings.Executable, settings.Arguments, lease.DirectoryPath, settings.Environment,
                    settings.ExpectedExecutableSha256, lifetimeCancellation, lease.ValidateForExecution);
                return new WindowsDuplexProcess(settings, process, lease, lifetimeCancellation);
            }
            catch
            {
                process?.Dispose();
                lease.Dispose();
                throw;
            }
        }, lifetimeCancellation).ConfigureAwait(false);
    }

    /// <summary>仅允许一个读循环；等待取消不会关闭进程，也不会丢弃已经接收的行。</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        if (settings.StandardOutputMode != WindowsDuplexProcessOutputMode.LineFrames) throw new WindowsDuplexProcessException("stdout_drain_mode");
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) throw new WindowsDuplexProcessException("single_reader_required");
        try
        {
            while (true)
            {
                lock (gate)
                {
                    if (lines.Count == 0 && readsComplete) return null;
                }

                await lineSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    if (lines.Count > 0) return lines.Dequeue();
                    if (readsComplete) return null;
                }
            }
        }
        finally { Interlocked.Exchange(ref reading, 0); }
    }

    /// <summary>
    /// 写入恰好一个 UTF-8 行帧。入队前的拒绝没有副作用；实际写入取消/超时会关闭通道，
    /// 异常 OperationMayHaveStarted=true 时不得自动重发业务请求。
    /// </summary>
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (line == null) throw new ArgumentNullException(nameof(line));
        if (line.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new WindowsDuplexProcessException("invalid_line");
        int byteCount;
        try { byteCount = Utf8.GetByteCount(line); }
        catch (EncoderFallbackException) { throw new WindowsDuplexProcessException("invalid_utf8"); }
        if (byteCount > settings.MaxLineBytes) throw new WindowsDuplexProcessException("line_limit");
        cancellationToken.ThrowIfCancellationRequested();
        if (!pendingWrites.Wait(0)) throw new WindowsDuplexProcessException("write_backpressure");
        var acquired = false;
        try
        {
            await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Utf8.GetBytes(line + "\n");
            Task io;
            lock (gate)
            {
                if (requestedStop.HasValue || Completion.IsCompleted) throw new WindowsDuplexProcessException("closed");
                io = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        if (!WindowsDuplexProcessNative.WriteFile(native.Input!, bytes, bytes.Length, out var written, IntPtr.Zero) || written != bytes.Length)
                            throw new WindowsDuplexProcessException("write_failed", true);
                    }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                activeWrite = io;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = Task.Delay(settings.WriteTimeout, deadline.Token);
            if (await Task.WhenAny(io, timeout).ConfigureAwait(false) != io)
            {
                RequestStop(cancellationToken.IsCancellationRequested ? WindowsDuplexProcessTermination.Canceled : WindowsDuplexProcessTermination.WriteTimedOut);
                try { NativeProcessMethods.CancelIoEx(native.Input!, IntPtr.Zero); }
                catch (ObjectDisposedException) { /* 关闭已先完成，由 job 回收解除阻塞。 */ }
                _ = io.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new WindowsDuplexProcessException(cancellationToken.IsCancellationRequested ? "write_canceled" : "write_timeout", true);
            }

            deadline.Cancel();
            try { await io.ConfigureAwait(false); }
            catch
            {
                RequestStop(WindowsDuplexProcessTermination.IoFailure);
                throw new WindowsDuplexProcessException("write_failed", true);
            }
        }
        finally
        {
            if (acquired) writer.Release();
            pendingWrites.Release();
        }
    }

    /// <summary>关闭 stdin，短暂等待自然退出后强制回收整个 job；重复关闭复用同一 Completion。</summary>
    public Task<WindowsDuplexProcessExit> CloseAsync()
    {
        RequestStop(WindowsDuplexProcessTermination.Closed);
        return Completion;
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    private void RequestStop(WindowsDuplexProcessTermination reason)
    {
        lock (gate)
        {
            if (!requestedStop.HasValue || (IsGraceful(requestedStop.Value) && !IsGraceful(reason))) requestedStop = reason;
        }
    }

    private static bool IsGraceful(WindowsDuplexProcessTermination reason) => reason == WindowsDuplexProcessTermination.Exited ||
        reason == WindowsDuplexProcessTermination.Closed || reason == WindowsDuplexProcessTermination.PeerClosed;

    private void ReadOutput()
    {
        var buffer = new byte[4096];
        using var pending = new MemoryStream();
        try
        {
            while (true)
            {
                if (!NativeProcessMethods.ReadFile(native.Output!, buffer, buffer.Length, out var count, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != NativeProcessMethods.BrokenPipe && error != NativeProcessMethods.OperationAborted) RequestStop(WindowsDuplexProcessTermination.IoFailure);
                    break;
                }
                if (count == 0) break;
                Interlocked.Add(ref stdoutBytes, count);
                if (settings.StandardOutputMode == WindowsDuplexProcessOutputMode.Drain)
                {
                    Array.Clear(buffer, 0, count);
                    continue;
                }
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == 10)
                    {
                        var bytes = pending.ToArray();
                        var length = bytes.Length > 0 && bytes[bytes.Length - 1] == 13 ? bytes.Length - 1 : bytes.Length;
                        var text = Utf8.GetString(bytes, 0, length);
                        lock (gate)
                        {
                            if (requestedStop.HasValue && !IsGraceful(requestedStop.Value)) return;
                            if (lines.Count >= settings.MaxPendingLines)
                            {
                                requestedStop = WindowsDuplexProcessTermination.OutputBackpressure;
                                return;
                            }

                            lines.Enqueue(text);
                            lineSignal.Release();
                        }

                        pending.SetLength(0);
                    }
                    else
                    {
                        // CRLF 的 CR 属于分隔符，不占正文预算；仅额外容许恰好一个尾 CR。
                        if (pending.Length >= settings.MaxLineBytes && !(pending.Length == settings.MaxLineBytes && buffer[index] == 13))
                        {
                            RequestStop(WindowsDuplexProcessTermination.LineLimitExceeded);
                            return;
                        }

                        pending.WriteByte(buffer[index]);
                    }
                }
            }

            if (pending.Length > 0) RequestStop(WindowsDuplexProcessTermination.TruncatedLine);
            else if (NativeProcessMethods.WaitForSingleObject(native.Process!, 0) != NativeProcessMethods.WaitObject)
                RequestStop(WindowsDuplexProcessTermination.PeerClosed);
        }
        catch (DecoderFallbackException) { RequestStop(WindowsDuplexProcessTermination.InvalidUtf8); }
        catch (Exception) { RequestStop(WindowsDuplexProcessTermination.IoFailure); }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            lock (gate)
            {
                readsComplete = true;
                lineSignal.Release();
            }
        }
    }

    private void ReadError()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                if (!NativeProcessMethods.ReadFile(native.Error!, buffer, buffer.Length, out var count, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != NativeProcessMethods.BrokenPipe && error != NativeProcessMethods.OperationAborted) RequestStop(WindowsDuplexProcessTermination.IoFailure);
                    break;
                }
                if (count == 0) break;
                if (Interlocked.Add(ref stderrBytes, count) > settings.MaxStandardErrorBytes && settings.StandardErrorOverflow == WindowsDuplexProcessErrorOverflow.Terminate)
                {
                    RequestStop(WindowsDuplexProcessTermination.StandardErrorLimitExceeded);
                    return;
                }

                Array.Clear(buffer, 0, count);
            }
        }
        catch (Exception) { RequestStop(WindowsDuplexProcessTermination.IoFailure); }
        finally { Array.Clear(buffer, 0, buffer.Length); }
    }

    private async Task<WindowsDuplexProcessExit> MonitorAsync()
    {
        WindowsDuplexProcessTermination reason;
        int? exitCode = null;
        var cleanupConfirmed = false;
        var ioSettled = false;
        try
        {
            while (true)
            {
                var wait = NativeProcessMethods.WaitForSingleObject(native.Process!, 0);
                lock (gate)
                {
                    if (requestedStop.HasValue) { reason = requestedStop.Value; break; }
                    if (wait == NativeProcessMethods.WaitObject) { reason = WindowsDuplexProcessTermination.Exited; break; }
                    if (wait != NativeProcessMethods.WaitTimeout) { reason = WindowsDuplexProcessTermination.IoFailure; break; }
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            lock (gate) { requestedStop ??= reason; }
            native.Input!.Dispose();
            if (reason == WindowsDuplexProcessTermination.Closed || reason == WindowsDuplexProcessTermination.PeerClosed)
            {
                var grace = Stopwatch.StartNew();
                while (grace.Elapsed < settings.CloseGracePeriod && NativeProcessMethods.WaitForSingleObject(native.Process!, 0) != NativeProcessMethods.WaitObject)
                    await Task.Delay(15).ConfigureAwait(false);
            }

            if (NativeProcessMethods.WaitForSingleObject(native.Process!, 0) == NativeProcessMethods.WaitObject &&
                NativeProcessMethods.GetExitCodeProcess(native.Process!, out var beforeKill)) exitCode = unchecked((int)beforeKill);
            NativeProcessMethods.TerminateJobObject(native.Job!, 0xC000013A);
            var cleanup = Stopwatch.StartNew();
            while (cleanup.Elapsed < settings.CleanupTimeout)
            {
                if (NativeProcessMethods.QueryInformationJobObject(native.Job!, 1, out var accounting,
                    Marshal.SizeOf<NativeProcessMethods.BasicAccountingInformation>(), IntPtr.Zero) && accounting.ActiveProcesses == 0 &&
                    NativeProcessMethods.WaitForSingleObject(native.Process!, 0) == NativeProcessMethods.WaitObject)
                { cleanupConfirmed = true; break; }
                await Task.Delay(15).ConfigureAwait(false);
            }

            if (!exitCode.HasValue && cleanupConfirmed && NativeProcessMethods.GetExitCodeProcess(native.Process!, out var code)) exitCode = unchecked((int)code);
            Task write;
            lock (gate) { write = activeWrite; }
            var pumps = Task.WhenAll(outputPump, errorPump, write);
            ioSettled = await Task.WhenAny(pumps, Task.Delay(settings.CleanupTimeout)).ConfigureAwait(false) == pumps;
            if (ioSettled)
            {
                try { await pumps.ConfigureAwait(false); }
                catch { /* 终结导致的 WriteFile 失败由具体写调用报告；不回显正文。 */ }
            }
            else
            {
                NativeProcessMethods.CancelIoEx(native.Output!, IntPtr.Zero);
                NativeProcessMethods.CancelIoEx(native.Error!, IntPtr.Zero);
                _ = pumps.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            lock (gate) { reason = requestedStop ?? reason; }
            return new WindowsDuplexProcessExit(cleanupConfirmed && ioSettled ? reason : WindowsDuplexProcessTermination.Unknown,
                reason, exitCode, cleanupConfirmed, ioSettled, Interlocked.Read(ref stderrBytes), Interlocked.Read(ref stdoutBytes),
                Interlocked.Read(ref stderrBytes) > settings.MaxStandardErrorBytes);
        }
        finally
        {
            lifetimeRegistration.Dispose();
            native.Dispose();
            directory.Dispose();
            lock (gate)
            {
                readsComplete = true;
                lineSignal.Release();
            }
        }
    }

    private sealed class Settings
    {
        internal Settings(WindowsDuplexProcessOptions options)
        {
            WindowsDuplexProcessNative.ValidatePath(options.TrustedExecutablePath);
            var expectedDigest = options.ExpectedExecutableSha256;
            if (expectedDigest != null)
            {
                if (expectedDigest.Length != 64) throw new ArgumentException("Expected executable digest must be SHA-256 hexadecimal.", nameof(options));
                foreach (var value in expectedDigest)
                    if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F')))
                        throw new ArgumentException("Expected executable digest must be SHA-256 hexadecimal.", nameof(options));
            }
            if (options.MaxLineBytes < 1 || options.MaxLineBytes > 16 * 1024 * 1024 ||
                options.MaxPendingLines < 1 || options.MaxPendingLines > 1024 ||
                options.MaxPendingWrites < 1 || options.MaxPendingWrites > 64 ||
                (long)options.MaxPendingLines * options.MaxLineBytes > 64 * 1024 * 1024 ||
                (long)options.MaxPendingWrites * options.MaxLineBytes > 64 * 1024 * 1024 ||
                options.MaxStandardErrorBytes < 0 || options.MaxStandardErrorBytes > 64 * 1024 * 1024 ||
                options.WriteTimeout <= TimeSpan.Zero || options.WriteTimeout > TimeSpan.FromMinutes(5) ||
                options.CleanupTimeout <= TimeSpan.Zero || options.CleanupTimeout > TimeSpan.FromMinutes(1) ||
                options.CloseGracePeriod < TimeSpan.Zero || options.CloseGracePeriod > TimeSpan.FromSeconds(5))
                throw new ArgumentOutOfRangeException(nameof(options));
            if (!Enum.IsDefined(typeof(WindowsDuplexProcessOutputMode), options.StandardOutputMode) ||
                !Enum.IsDefined(typeof(WindowsDuplexProcessErrorOverflow), options.StandardErrorOverflow)) throw new ArgumentOutOfRangeException(nameof(options));
            Executable = options.TrustedExecutablePath;
            ExpectedExecutableSha256 = expectedDigest?.ToLowerInvariant();
            var arguments = new string[options.Arguments.Count];
            for (var index = 0; index < arguments.Length; index++) arguments[index] = options.Arguments[index] ?? throw new ArgumentException("Null argument.", nameof(options));
            Arguments = arguments;
            Environment = new Dictionary<string, string>(options.Environment, StringComparer.OrdinalIgnoreCase);
            MaxLineBytes = options.MaxLineBytes;
            MaxPendingLines = options.MaxPendingLines;
            MaxPendingWrites = options.MaxPendingWrites;
            MaxStandardErrorBytes = options.MaxStandardErrorBytes;
            WriteTimeout = options.WriteTimeout;
            CleanupTimeout = options.CleanupTimeout;
            CloseGracePeriod = options.CloseGracePeriod;
            StandardOutputMode = options.StandardOutputMode;
            StandardErrorOverflow = options.StandardErrorOverflow;
        }

        internal string Executable { get; }
        internal string? ExpectedExecutableSha256 { get; }
        internal IReadOnlyList<string> Arguments { get; }
        internal IDictionary<string, string> Environment { get; }
        internal int MaxLineBytes { get; }
        internal int MaxPendingLines { get; }
        internal int MaxPendingWrites { get; }
        internal long MaxStandardErrorBytes { get; }
        internal TimeSpan WriteTimeout { get; }
        internal TimeSpan CloseGracePeriod { get; }
        internal WindowsDuplexProcessOutputMode StandardOutputMode { get; }
        internal WindowsDuplexProcessErrorOverflow StandardErrorOverflow { get; }
        internal TimeSpan CleanupTimeout { get; }
    }
}
