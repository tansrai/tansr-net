using System.Text.Json;

namespace Tansr.Sdk.Execution;

/// <summary>原操作的旁路输出捕获；不授予执行权限，也不替代原终态回执。</summary>
public interface IExecutionOutputSink
{
    Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken cancellationToken);
}

public interface IExecutionOutputCapture : IDisposable
{
    /// <summary>同步、有界捕获原始字节；不得等待网络。false 表示截断，调用方仍须排空进程管道。</summary>
    bool Append(string channel, string encoding, byte[] rawBytes);
    /// <summary>封口并等待本次输送终止。封口不表示进程成功；提交未知不得重跑原操作。</summary>
    Task SealAsync(bool captureTruncated, CancellationToken cancellationToken);
    Task Completion { get; }
}
