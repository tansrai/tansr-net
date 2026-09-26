using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>
/// Windows 前台进程后端。主线程恢复前加入不可脱离的 Job Object，结束时回收整个进程树。
/// 工作区只固定 cwd 身份，不是操作系统沙箱；权限、解释器/参数批准由可信宿主负责。
/// </summary>
public sealed class WindowsProcessExecutor
{
    private readonly SemaphoreSlim outputHandlerSlots;

    /// <summary>复用同一执行器以共享容量；已超时但未结束的宿主回调仍占槽，不能因停止等待就释放。</summary>
    public WindowsProcessExecutor(int maximumOutstandingOutputHandlers = 8)
    {
        if (maximumOutstandingOutputHandlers < 1 || maximumOutstandingOutputHandlers > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutstandingOutputHandlers));
        }

        outputHandlerSlots = new SemaphoreSlim(maximumOutstandingOutputHandlers, maximumOutstandingOutputHandlers);
    }

    public async Task<WindowsProcessResult> ExecuteAsync(
        WindowsProcessRequest request,
        WindowsProcessOutputHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            throw new PlatformNotSupportedException("This process backend requires Windows.");
        }

        var options = new ExecutionOptions(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return new WindowsProcessResult(WindowsProcessTermination.Canceled, WindowsProcessTermination.Canceled,
                null, null, false, true, true, string.Empty, string.Empty, 0, null);
        }

        using var outputReservation = outputHandler == null ? null : ReserveOutputHandler();
        if (outputHandler != null && outputReservation == null)
        {
            // 无容量时不启动新进程，不能先产生副作用再告诉宿主资源已耗尽。
            return new WindowsProcessResult(WindowsProcessTermination.OutputBackpressure, WindowsProcessTermination.OutputBackpressure,
                null, null, false, true, false, string.Empty, string.Empty, 0, null);
        }

        using var directory = request.AcquireWorkingDirectory() ?? throw new InvalidOperationException("The working directory lease is missing.");
        directory.ValidateForExecution();
        ValidateLocalAbsolutePath(directory.DirectoryPath, false);
        using var process = NativeProcessLaunch.Start(options.Executable, options.Arguments, directory.DirectoryPath, options.Environment,
            options.ExpectedExecutableSha256, cancellationToken, options.ValidateBeforeStart);
        if (!process.Started)
        {
            return new WindowsProcessResult(process.CleanupConfirmed ? WindowsProcessTermination.StartFailed : WindowsProcessTermination.Unknown,
                WindowsProcessTermination.StartFailed, process.ProcessId, null, false, process.CleanupConfirmed, true,
                string.Empty, string.Empty, 0, process.StartError);
        }

        var capture = new OutputCapture(options, outputHandler != null);
        using var callbackCancellation = new CancellationTokenSource();
        var stdoutTask = Task.Run(() => capture.Read(process.StandardOutput!, WindowsProcessOutputStream.StandardOutput));
        var stderrTask = Task.Run(() => capture.Read(process.StandardError!, WindowsProcessOutputStream.StandardError));
        var readers = Task.WhenAll(stdoutTask, stderrTask);
        var consumer = outputHandler == null ? Task.CompletedTask : Task.Run(() => capture.DeliverAsync(outputHandler, callbackCancellation.Token));
        outputReservation?.ReleaseAfter(consumer, () => capture.LastCallback);
        var elapsed = Stopwatch.StartNew();
        var reason = WindowsProcessTermination.Exited;
        int? nativeError = null;
        int? exitCode = null;
        var cleanupConfirmed = false;

        try
        {
            options.Started?.Invoke(process.ProcessId!.Value);
            while (true)
            {
                var status = NativeProcessMethods.WaitForSingleObject(process.Process!, 0);
                if (status == NativeProcessMethods.WaitObject)
                {
                    if (NativeProcessMethods.GetExitCodeProcess(process.Process!, out var code))
                    {
                        exitCode = unchecked((int)code);
                    }
                    else
                    {
                        nativeError = Marshal.GetLastWin32Error();
                        reason = WindowsProcessTermination.NativeFailure;
                    }

                    break;
                }

                if (status != NativeProcessMethods.WaitTimeout)
                {
                    nativeError = Marshal.GetLastWin32Error();
                    reason = WindowsProcessTermination.NativeFailure;
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    reason = WindowsProcessTermination.Canceled;
                    break;
                }

                if (capture.StopReason.HasValue)
                {
                    reason = capture.StopReason.Value;
                    break;
                }

                if (elapsed.Elapsed >= options.Timeout)
                {
                    reason = WindowsProcessTermination.TimedOut;
                    break;
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            // 即使主进程正常退出，前台调用也不得留下持有输出管道的子孙进程。
            if (!NativeProcessMethods.TerminateJobObject(process.Job!, 0xC000013A))
            {
                nativeError = Marshal.GetLastWin32Error();
            }

            var cleanup = Stopwatch.StartNew();
            while (cleanup.Elapsed < options.CleanupTimeout)
            {
                if (NativeProcessMethods.QueryInformationJobObject(process.Job!, 1, out var accounting,
                    Marshal.SizeOf<NativeProcessMethods.BasicAccountingInformation>(), IntPtr.Zero) &&
                    accounting.ActiveProcesses == 0 &&
                    NativeProcessMethods.WaitForSingleObject(process.Process!, 0) == NativeProcessMethods.WaitObject)
                {
                    cleanupConfirmed = true;
                    break;
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            if (!exitCode.HasValue && cleanupConfirmed && NativeProcessMethods.GetExitCodeProcess(process.Process!, out var finalCode))
            {
                exitCode = unchecked((int)finalCode);
            }

            if (await Task.WhenAny(readers, Task.Delay(options.CleanupTimeout)).ConfigureAwait(false) != readers)
            {
                // 不在父端停读等待消费者；pipe 有独立排空线程。取消是清理失败的兜底。
                NativeProcessMethods.CancelIoEx(process.StandardOutput!, IntPtr.Zero);
                NativeProcessMethods.CancelIoEx(process.StandardError!, IntPtr.Zero);
                capture.Stop(WindowsProcessTermination.NativeFailure);
            }
            else
            {
                try
                {
                    await readers.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    capture.Stop(WindowsProcessTermination.NativeFailure);
                }
            }

            capture.Complete();
            if (await Task.WhenAny(consumer, Task.Delay(options.OutputCallbackTimeout)).ConfigureAwait(false) != consumer)
            {
                capture.Stop(WindowsProcessTermination.OutputCallbackFailed);
                CancelOutputCallbacks(callbackCancellation, capture);
            }

            if (reason == WindowsProcessTermination.Exited && capture.StopReason.HasValue)
            {
                reason = capture.StopReason.Value;
            }

            return capture.Result(cleanupConfirmed ? reason : WindowsProcessTermination.Unknown, reason,
                process.ProcessId, exitCode, cleanupConfirmed,
                readers.IsCompleted && consumer.IsCompleted, consumer.IsCompleted, nativeError ?? capture.NativeError);
        }
        finally
        {
            // 包括宿主取消/回调异常在内的每个出口都保有 job 句柄直至终结尝试完成。
            if (!cleanupConfirmed)
            {
                NativeProcessMethods.TerminateJobObject(process.Job!, 0xC000013A);
            }

            capture.Complete();
            CancelOutputCallbacks(callbackCancellation, capture);
            _ = Task.WhenAll(readers, consumer).ContinueWith(completed =>
            {
                _ = completed.Exception;
                capture.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private OutputHandlerReservation? ReserveOutputHandler() =>
        outputHandlerSlots.Wait(0) ? new OutputHandlerReservation(outputHandlerSlots) : null;

    private sealed class OutputHandlerReservation : IDisposable
    {
        private readonly SemaphoreSlim slots;
        private bool transferred;

        internal OutputHandlerReservation(SemaphoreSlim slots) => this.slots = slots;

        internal void ReleaseAfter(Task consumer, Func<Task> lastCallback)
        {
            transferred = true;
            _ = WaitForActualCompletionAsync(consumer, lastCallback);
        }

        private async Task WaitForActualCompletionAsync(Task consumer, Func<Task> lastCallback)
        {
            try
            {
                try { await consumer.ConfigureAwait(false); }
                catch (Exception) { /* 输出失败已由结果记录，不向默认日志泄漏宿主异常正文。 */ }
                try { await lastCallback().ConfigureAwait(false); }
                catch (Exception) { /* 即使晚到失败，只有实际 Task 结束才归还容量。 */ }
            }
            finally
            {
                slots.Release();
            }
        }

        public void Dispose()
        {
            if (!transferred)
            {
                slots.Release();
                transferred = true;
            }
        }
    }

    private static void CancelOutputCallbacks(CancellationTokenSource cancellation, OutputCapture capture)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // 宿主注册的取消回调异常不能阻止 job/pipe/cwd 租约清理。
            capture.Stop(WindowsProcessTermination.OutputCallbackFailed);
        }
    }

    private static void ValidateLocalAbsolutePath(string value, bool executable)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' ||
            value.IndexOfAny(new[] { '\0', '/', '"', '*', '?' }) >= 0 || value.IndexOf(':', 2) >= 0 ||
            !string.Equals(Path.GetFullPath(value), value, StringComparison.OrdinalIgnoreCase) ||
            (executable && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A canonical absolute local Windows path is required.", nameof(value));
        }
    }

    private sealed class ExecutionOptions
    {
        internal ExecutionOptions(WindowsProcessRequest request)
        {
            ValidateLocalAbsolutePath(request.TrustedExecutablePath, true);
            if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromDays(1) ||
                request.CleanupTimeout <= TimeSpan.Zero || request.CleanupTimeout > TimeSpan.FromMinutes(1) ||
                request.OutputCallbackTimeout <= TimeSpan.Zero || request.OutputCallbackTimeout > TimeSpan.FromMinutes(1) ||
                request.MaxOutputBytes < 1 || request.MaxOutputBytes > 64 * 1024 * 1024 ||
                request.ChunkBytes < 1 || request.ChunkBytes > 64 * 1024 || request.MaxPendingChunks < 1 || request.MaxPendingChunks > 1024 ||
                (long)request.ChunkBytes * request.MaxPendingChunks > 4 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "The process resource limits are invalid.");
            }

            Executable = request.TrustedExecutablePath;
            if (request.ExpectedExecutableSha256 != null && (request.ExpectedExecutableSha256.Length != 64 ||
                request.ExpectedExecutableSha256.Any(value => !(value >= '0' && value <= '9' || value >= 'a' && value <= 'f'))))
                throw new ArgumentException("An expected executable SHA256 must be 64 lowercase hexadecimal characters.", nameof(request));
            ExpectedExecutableSha256 = request.ExpectedExecutableSha256;
            var arguments = new string[request.Arguments.Count];
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = request.Arguments[index] ?? throw new ArgumentException("An argument is null.", nameof(request));
            }

            Arguments = arguments;
            Environment = new Dictionary<string, string>(request.Environment, StringComparer.OrdinalIgnoreCase);
            Timeout = request.Timeout;
            CleanupTimeout = request.CleanupTimeout;
            OutputCallbackTimeout = request.OutputCallbackTimeout;
            MaxOutputBytes = request.MaxOutputBytes;
            ChunkBytes = request.ChunkBytes;
            MaxPendingChunks = request.MaxPendingChunks;
            Started = request.Started;
            DrainAfterOutputLimit = request.DrainAfterOutputLimit;
            OutputTruncated = request.OutputTruncated;
            ValidateBeforeStart = request.ValidateBeforeStart;
        }

        internal string Executable { get; }
        internal string? ExpectedExecutableSha256 { get; }
        internal IReadOnlyList<string> Arguments { get; }
        internal IDictionary<string, string> Environment { get; }
        internal TimeSpan Timeout { get; }
        internal TimeSpan CleanupTimeout { get; }
        internal TimeSpan OutputCallbackTimeout { get; }
        internal int MaxOutputBytes { get; }
        internal int ChunkBytes { get; }
        internal int MaxPendingChunks { get; }
        internal Action<int>? Started { get; }
        internal bool DrainAfterOutputLimit { get; }
        internal Action? OutputTruncated { get; }
        internal Action? ValidateBeforeStart { get; }
    }

    private sealed class OutputCapture : IDisposable
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
        private readonly object gate = new();
        private readonly ExecutionOptions options;
        private readonly bool queueEnabled;
        private readonly BlockingCollection<WindowsProcessOutputChunk> queue;
        private readonly MemoryStream stdout = new();
        private readonly MemoryStream stderr = new();
        private readonly Decoder stdoutDecoder = Utf8.GetDecoder();
        private readonly Decoder stderrDecoder = Utf8.GetDecoder();
        private long observed;
        private long sequence;
        private WindowsProcessTermination? stopped;
        private int? nativeError;
        private bool complete;
        private bool deliveryFailed;
        private bool outputTruncated;
        private Task lastCallback = Task.CompletedTask;

        internal OutputCapture(ExecutionOptions options, bool queueEnabled)
        {
            this.options = options;
            this.queueEnabled = queueEnabled;
            queue = new BlockingCollection<WindowsProcessOutputChunk>(options.MaxPendingChunks);
        }

        internal WindowsProcessTermination? StopReason
        {
            get { lock (gate) { return stopped; } }
        }

        internal int? NativeError
        {
            get { lock (gate) { return nativeError; } }
        }

        internal Task LastCallback
        {
            get { lock (gate) { return lastCallback; } }
        }

        internal void Stop(WindowsProcessTermination reason)
        {
            lock (gate)
            {
                stopped ??= reason;
            }
        }

        internal void Read(SafeFileHandle pipe, WindowsProcessOutputStream stream)
        {
            var buffer = new byte[options.ChunkBytes];
            try
            {
                while (true)
                {
                    if (!NativeProcessMethods.ReadFile(pipe, buffer, buffer.Length, out var size, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != NativeProcessMethods.BrokenPipe && error != NativeProcessMethods.OperationAborted)
                        {
                            lock (gate)
                            {
                                nativeError = error;
                                stopped ??= WindowsProcessTermination.NativeFailure;
                            }
                        }

                        break;
                    }

                    if (size == 0)
                    {
                        break;
                    }

                    Append(stream, buffer, size, false);
                }

                Append(stream, Array.Empty<byte>(), 0, true);
            }
            catch (Exception error) when (error is ObjectDisposedException || error is IOException)
            {
                Stop(WindowsProcessTermination.NativeFailure);
            }
        }

        private void Append(WindowsProcessOutputStream stream, byte[] buffer, int size, bool flush)
        {
            lock (gate)
            {
                observed += size;
                if (complete)
                {
                    return;
                }

                var available = options.MaxOutputBytes - (int)(stdout.Length + stderr.Length);
                var accepted = Math.Min(available, size);
                if (accepted < size)
                {
                    if (!outputTruncated) options.OutputTruncated?.Invoke();
                    outputTruncated = true;
                    if (!options.DrainAfterOutputLimit) stopped ??= WindowsProcessTermination.OutputLimitExceeded;
                }

                var destination = stream == WindowsProcessOutputStream.StandardOutput ? stdout : stderr;
                var decoder = stream == WindowsProcessOutputStream.StandardOutput ? stdoutDecoder : stderrDecoder;
                var offset = destination.Length;
                destination.Write(buffer, 0, accepted);
                var characters = new char[Utf8.GetMaxCharCount(accepted)];
                var count = decoder.GetChars(buffer, 0, accepted, characters, 0, flush);
                if (queueEnabled && !deliveryFailed && (accepted > 0 || count > 0))
                {
                    var chunk = new WindowsProcessOutputChunk(++sequence, stream, offset, buffer, accepted, new string(characters, 0, count));
                    if (!queue.TryAdd(chunk))
                    {
                        options.OutputTruncated?.Invoke();
                        if (!options.DrainAfterOutputLimit) stopped ??= WindowsProcessTermination.OutputBackpressure;
                        deliveryFailed = true;
                    }
                }
            }
        }

        internal async Task DeliverAsync(WindowsProcessOutputHandler handler, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var chunk in queue.GetConsumingEnumerable(cancellationToken))
                {
                    // Task.Run 也隔离错误的同步阻塞回调；每次最多一个在途回调，不为每块创建无界任务。
                    var delivery = Task.Run(() => handler(chunk, cancellationToken), cancellationToken);
                    lock (gate) { lastCallback = delivery; }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (await Task.WhenAny(delivery, Task.Delay(options.OutputCallbackTimeout, deadline.Token)).ConfigureAwait(false) != delivery)
                    {
                        Stop(WindowsProcessTermination.OutputCallbackFailed);
                        _ = delivery.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        return;
                    }

                    // 每块完成即撤销定时器；高吞吐时不积攒整个超时窗口内的历史定时器。
                    deadline.Cancel();
                    await delivery.ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 不输出异常正文，回调可能包含命令参数或秘密。
                Stop(WindowsProcessTermination.OutputCallbackFailed);
            }
        }

        internal void Complete()
        {
            lock (gate)
            {
                if (!complete)
                {
                    complete = true;
                    queue.CompleteAdding();
                }
            }
        }

        internal WindowsProcessResult Result(WindowsProcessTermination termination, WindowsProcessTermination requested,
            int? processId, int? exitCode, bool cleanupConfirmed, bool pumpsComplete, bool consumerComplete, int? error)
        {
            lock (gate)
            {
                var stdoutBytes = stdout.ToArray(); var stderrBytes = stderr.ToArray();
                return new WindowsProcessResult(termination, requested, processId, exitCode, true, cleanupConfirmed,
                    pumpsComplete && !stopped.HasValue && !deliveryFailed && !outputTruncated,
                    Utf8.GetString(stdoutBytes), Utf8.GetString(stderrBytes), observed, error,
                    consumerComplete && lastCallback.IsCompleted, stdoutBytes, stderrBytes);
            }
        }

        public void Dispose()
        {
            // 仅在两个读线程和队列消费都结束后释放，不与未完成的 ReadFile 竞争。
            lock (gate)
            {
                queue.Dispose();
                stdout.Dispose();
                stderr.Dispose();
            }
        }
    }
}
