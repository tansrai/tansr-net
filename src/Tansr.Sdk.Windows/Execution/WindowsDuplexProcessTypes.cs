using System;
using System.Collections.Generic;
using System.IO;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>可信宿主批准的持久 stdio 程序；这不是 OS 沙箱，不接受模型选择解释器或环境。</summary>
public sealed class WindowsDuplexProcessOptions
{
    public WindowsDuplexProcessOptions(string trustedExecutablePath, IReadOnlyList<string> arguments,
        Func<IWindowsProcessWorkingDirectoryLease> acquireWorkingDirectory)
    {
        TrustedExecutablePath = trustedExecutablePath ?? throw new ArgumentNullException(nameof(trustedExecutablePath));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        AcquireWorkingDirectory = acquireWorkingDirectory ?? throw new ArgumentNullException(nameof(acquireWorkingDirectory));
    }

    public string TrustedExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public Func<IWindowsProcessWorkingDirectoryLease> AcquireWorkingDirectory { get; }
    /// <summary>完整显式环境，不继承宿主 PATH、令牌或密钥。</summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>可选的获准 exe SHA-256；在全部路径和文件句柄固定后，从同一文件对象校验再启动。</summary>
    public string? ExpectedExecutableSha256 { get; set; }
    public int MaxLineBytes { get; set; } = 1024 * 1024;
    public int MaxPendingLines { get; set; } = 16;
    public int MaxPendingWrites { get; set; } = 8;
    /// <summary>累计 stderr 安全帽；超过后关闭进程，不保存或在异常中展示正文。</summary>
    public long MaxStandardErrorBytes { get; set; } = 1024 * 1024;
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan CloseGracePeriod { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>LineFrames 用于 MCP；Drain 用于受控服务日志，固定缓冲排空且不保留正文。</summary>
    public WindowsDuplexProcessOutputMode StandardOutputMode { get; set; } = WindowsDuplexProcessOutputMode.LineFrames;
    public WindowsDuplexProcessErrorOverflow StandardErrorOverflow { get; set; } = WindowsDuplexProcessErrorOverflow.Terminate;
}

public enum WindowsDuplexProcessOutputMode { LineFrames, Drain }
public enum WindowsDuplexProcessErrorOverflow { Terminate, Drain }

public enum WindowsDuplexProcessTermination
{
    Exited,
    Closed,
    Canceled,
    PeerClosed,
    WriteTimedOut,
    LineLimitExceeded,
    OutputBackpressure,
    StandardErrorLimitExceeded,
    InvalidUtf8,
    TruncatedLine,
    IoFailure,
    Unknown,
}

public sealed class WindowsDuplexProcessExit
{
    internal WindowsDuplexProcessExit(WindowsDuplexProcessTermination termination, WindowsDuplexProcessTermination requested,
        int? exitCode, bool cleanupConfirmed, bool ioSettled, long stderrBytes, long stdoutBytes, bool stderrTruncated)
    {
        Termination = termination;
        RequestedTermination = requested;
        ExitCode = exitCode;
        CleanupConfirmed = cleanupConfirmed;
        IoSettled = ioSettled;
        StandardErrorBytes = stderrBytes;
        StandardOutputBytes = stdoutBytes;
        StandardErrorTruncated = stderrTruncated;
    }

    public WindowsDuplexProcessTermination Termination { get; }
    public WindowsDuplexProcessTermination RequestedTermination { get; }
    public int? ExitCode { get; }
    public bool CleanupConfirmed { get; }
    public bool IoSettled { get; }
    public long StandardErrorBytes { get; }
    public long StandardOutputBytes { get; }
    public bool StandardErrorTruncated { get; }
}

/// <summary>稳定错误码；不附加命令、JSON、环境变量或 stderr 正文。</summary>
public sealed class WindowsDuplexProcessException : IOException
{
    public WindowsDuplexProcessException(string code, bool operationMayHaveStarted = false)
        : base("Windows stdio process failed: " + code)
    {
        Code = code;
        OperationMayHaveStarted = operationMayHaveStarted;
    }

    public string Code { get; }
    /// <summary>写入可能部分完成；必须对账或关闭会话，不能自动重新发送业务请求。</summary>
    public bool OperationMayHaveStarted { get; }
}
