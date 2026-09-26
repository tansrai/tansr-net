using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>可信工作区提供的目录租约；持有目录及祖先身份，防止运行期目录替换。</summary>
public interface IWindowsProcessWorkingDirectoryLease : IDisposable
{
    string DirectoryPath { get; }

    void ValidateForExecution();
}

/// <summary>
/// 已获本地权限批准的进程请求。解释器及参数必须由可信宿主批准，不能直接采用模型声明。
/// 本执行器不提供文件系统/网络沙箱，不提升管理员权限，不运行 ShellExecute 或隐式 shell。
/// </summary>
public sealed class WindowsProcessRequest
{
    public WindowsProcessRequest(
        string trustedExecutablePath,
        IReadOnlyList<string> arguments,
        Func<IWindowsProcessWorkingDirectoryLease> acquireWorkingDirectory)
    {
        TrustedExecutablePath = trustedExecutablePath ?? throw new ArgumentNullException(nameof(trustedExecutablePath));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        AcquireWorkingDirectory = acquireWorkingDirectory ?? throw new ArgumentNullException(nameof(acquireWorkingDirectory));
    }

    public string TrustedExecutablePath { get; }

    public IReadOnlyList<string> Arguments { get; }

    public Func<IWindowsProcessWorkingDirectoryLease> AcquireWorkingDirectory { get; }

    /// <summary>仅传入明确批准的环境变量，不继承进程环境、PATH、令牌或密钥。</summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

    public int MaxOutputBytes { get; set; } = 65_536;

    public int MaxPendingChunks { get; set; } = 32;

    public int ChunkBytes { get; set; } = 4096;

    /// <summary>消费者必须响应取消；超时后终止子进程，并明确报告未完成输出交付。</summary>
    public TimeSpan OutputCallbackTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // 后台适配复用同一原生启动/Job链，见证由宿主内部消费，不是远端可设置参数。
    internal Action<int>? Started { get; set; }
    internal bool DrainAfterOutputLimit { get; set; }
    internal Action? OutputTruncated { get; set; }
}

public enum WindowsProcessOutputStream
{
    StandardOutput,
    StandardError,
}

/// <summary>本地执行器增量；Sequence 仅为本次调用内的顺序，不是任何 wire 序号。</summary>
public sealed class WindowsProcessOutputChunk
{
    internal WindowsProcessOutputChunk(long sequence, WindowsProcessOutputStream stream, long byteOffset, byte[] bytes, int byteCount, string text)
    {
        Sequence = sequence;
        Stream = stream;
        ByteOffset = byteOffset;
        ByteCount = byteCount;
        Text = text;
        var copy = new byte[byteCount];
        Buffer.BlockCopy(bytes, 0, copy, 0, byteCount);
        RawBytes = Array.AsReadOnly(copy);
    }

    public long Sequence { get; }

    public WindowsProcessOutputStream Stream { get; }

    public long ByteOffset { get; }

    public int ByteCount { get; }

    /// <summary>独立只读副本，保留非法 UTF-8/二进制字节，可供协议层做摘要及无损持久化。</summary>
    public IReadOnlyList<byte> RawBytes { get; }

    /// <summary>按流独立增量解码的 UTF-8 文本；不完整/非法 UTF-8 使用替代字符。</summary>
    public string Text { get; }
}

public enum WindowsProcessTermination
{
    Exited,
    Canceled,
    TimedOut,
    OutputLimitExceeded,
    OutputBackpressure,
    OutputCallbackFailed,
    NativeFailure,
    StartFailed,
    Unknown,
}

/// <summary>操作系统进程结果。已确认退出不等于业务副作用成功；未知结果禁止自动重做命令。</summary>
public sealed class WindowsProcessResult
{
    internal WindowsProcessResult(
        WindowsProcessTermination termination, WindowsProcessTermination requestedTermination,
        int? processId, int? exitCode, bool started, bool cleanupConfirmed, bool outputComplete,
        string standardOutput, string standardError, long outputBytesObserved, int? nativeErrorCode, bool outputDeliverySettled = true)
    {
        Termination = termination;
        RequestedTermination = requestedTermination;
        ProcessId = processId;
        ExitCode = exitCode;
        Started = started;
        CleanupConfirmed = cleanupConfirmed;
        OutputComplete = outputComplete;
        StandardOutput = standardOutput;
        StandardError = standardError;
        OutputBytesObserved = outputBytesObserved;
        NativeErrorCode = nativeErrorCode;
        OutputDeliverySettled = outputDeliverySettled;
    }

    public WindowsProcessTermination Termination { get; }

    public WindowsProcessTermination RequestedTermination { get; }

    public int? ProcessId { get; }

    public int? ExitCode { get; }

    /// <summary>已恢复主线程；一旦为 true 就不能因响应缺失而自动重新执行。</summary>
    public bool Started { get; }

    public bool CleanupConfirmed { get; }

    public bool OutputComplete { get; }

    /// <summary>宿主输出回调是否真正结束；忽略取消的回调为 false，且继续占用执行器容量。</summary>
    public bool OutputDeliverySettled { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public long OutputBytesObserved { get; }

    public int? NativeErrorCode { get; }
}

/// <summary>消费者须快速处理或异步排队，并响应 cancellationToken；执行器不会无界缓存慢消费者输出。</summary>
public delegate Task WindowsProcessOutputHandler(WindowsProcessOutputChunk chunk, CancellationToken cancellationToken);
