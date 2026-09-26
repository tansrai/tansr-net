using System.Text.RegularExpressions;

namespace Tansr.Sdk.Windows.Execution;

public enum WindowsSandboxMode { Off, On, Required }
public enum WindowsSandboxCapability { None, Partial, Full }

/// <summary>可信宿主的策略；网络/写入是否由 OS 强制执行取决于实际 runtime 的能力。</summary>
public sealed class WindowsSandboxPolicy
{
    public WindowsSandboxPolicy(IReadOnlyList<string> allowedWriteRoots, bool allowNetwork)
    { AllowedWriteRoots = Array.AsReadOnly((allowedWriteRoots ?? throw new ArgumentNullException(nameof(allowedWriteRoots))).ToArray()); AllowNetwork = allowNetwork; }
    public IReadOnlyList<string> AllowedWriteRoots { get; }
    public bool AllowNetwork { get; }
}

/// <summary>由开发者注入并信任的本机适配器；不得从模型响应加载程序集、路径或 runtime 实现。</summary>
public interface IWindowsShellSandboxRuntime
{
    string Id { get; }
    WindowsSandboxCapability Capability { get; }
    string CapabilityReason { get; }
    WindowsProcessRequest Prepare(WindowsProcessRequest approvedProcess, string workingDirectory, WindowsSandboxPolicy policy);
}

public sealed class WindowsShellSandboxSettings
{
    public WindowsShellSandboxSettings(WindowsSandboxMode mode, WindowsSandboxPolicy policy, IWindowsShellSandboxRuntime? runtime = null)
    { Mode = mode; Policy = policy ?? throw new ArgumentNullException(nameof(policy)); Runtime = runtime; }
    public WindowsSandboxMode Mode { get; }
    public WindowsSandboxPolicy Policy { get; }
    public IWindowsShellSandboxRuntime? Runtime { get; }
}

/// <summary>与原 Electron Windows 实现一致：环境清洗、cwd 约束，partial；不强制 OS 文件或网络隔离。</summary>
public sealed class WindowsHygieneSandboxRuntime : IWindowsShellSandboxRuntime
{
    private static readonly Regex Sensitive = new(@"(?:^|_)(?:API_?KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIALS?|ACCESS_KEY|PRIVATE_KEY|AUTH)(?:$|_)|(?:API_?KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIALS?|ACCESS_KEY|PRIVATE_KEY|AUTH)$|(?:^|_)KEY$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public string Id => "win32-hygiene";
    public WindowsSandboxCapability Capability => WindowsSandboxCapability.Partial;
    public string CapabilityReason => "env_hygiene_only";

    public WindowsProcessRequest Prepare(WindowsProcessRequest approvedProcess, string workingDirectory, WindowsSandboxPolicy policy)
    {
        WindowsDuplexProcessNative.ValidatePath(workingDirectory);
        foreach (var root in policy.AllowedWriteRoots) WindowsDuplexProcessNative.ValidatePath(root);
        var normalized = workingDirectory.TrimEnd('\\');
        if (!policy.AllowedWriteRoots.Any(root => string.Equals(root.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new Tansr.Sdk.Execution.ExecutionRejectedException("ENOTSUP");
        var result = new WindowsProcessRequest(approvedProcess.TrustedExecutablePath, approvedProcess.Arguments, approvedProcess.AcquireWorkingDirectory)
        {
            ExpectedExecutableSha256 = approvedProcess.ExpectedExecutableSha256,
            Timeout = approvedProcess.Timeout,
            MaxOutputBytes = approvedProcess.MaxOutputBytes,
            MaxPendingChunks = approvedProcess.MaxPendingChunks,
            ChunkBytes = approvedProcess.ChunkBytes,
            OutputCallbackTimeout = approvedProcess.OutputCallbackTimeout,
            CleanupTimeout = approvedProcess.CleanupTimeout
        };
        foreach (var item in approvedProcess.Environment) if (!Sensitive.IsMatch(item.Key)) result.Environment.Add(item.Key, item.Value);
        return result;
    }
}

/// <summary>仅可信 runtime 在明确未执行副作用时报告结构化拒绝；不能根据 stderr 或普通 EACCES 猜测。</summary>
public sealed class WindowsSandboxIsolationDeniedException : IOException
{
    public WindowsSandboxIsolationDeniedException() : base("sandbox_isolation_denied") { }
}
