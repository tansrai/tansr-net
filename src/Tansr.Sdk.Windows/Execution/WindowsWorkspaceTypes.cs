using System;
using System.Collections.Generic;
using System.IO;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>可信宿主决定的工作区写入策略；它不是操作系统沙箱。</summary>
public sealed class WindowsWorkspaceOptions
{
    /// <summary>
    /// 仅当所有写者（包括导入、编辑器和子进程）均通过同一工作区实例时启用。
    /// 启用后验证用户私有 ACL、持有独占写者锁并提供协作式条件提交；不能防绕过 SDK 的同用户写者。
    /// </summary>
    public bool AllWritersCooperate { get; set; }

    public int MaximumReadBytes { get; set; } = 4 * 1024 * 1024;
    public int MaximumWriteBytes { get; set; } = 4 * 1024 * 1024;
    public int MaximumEntries { get; set; } = 10000;
}

public enum WindowsWorkspaceEntryKind { File, Directory, ReparsePoint }

public sealed class WindowsWorkspaceEntry
{
    internal WindowsWorkspaceEntry(string path, WindowsWorkspaceEntryKind kind, long length, DateTime lastWriteTimeUtc)
    { RelativePath = path; Kind = kind; Length = length; LastWriteTimeUtc = lastWriteTimeUtc; }
    public string RelativePath { get; }
    public WindowsWorkspaceEntryKind Kind { get; }
    public long Length { get; }
    public DateTime LastWriteTimeUtc { get; }
}

public sealed class WindowsWorkspaceWriteResult
{
    internal WindowsWorkspaceWriteResult(string hash, long length) { Hash = hash; Length = length; }
    public string Hash { get; }
    public long Length { get; }
}

public sealed class WindowsWorkspaceSearchMatch
{
    internal WindowsWorkspaceSearchMatch(string path, int line, string text)
    { RelativePath = path; Line = line; Text = text; }
    public string RelativePath { get; }
    public int Line { get; }
    public string Text { get; }
}

public sealed class WindowsWorkspaceSearchResult
{
    internal WindowsWorkspaceSearchResult(IReadOnlyList<WindowsWorkspaceSearchMatch> matches, bool truncated)
    { Matches = matches; Truncated = truncated; }
    public IReadOnlyList<WindowsWorkspaceSearchMatch> Matches { get; }
    public bool Truncated { get; }
}

/// <summary>代码稳定，不在默认异常消息中回显原始路径或文件正文。</summary>
public sealed class WindowsWorkspaceException : IOException
{
    public WindowsWorkspaceException(string code) : base("Windows workspace operation failed: " + code) { Code = code; }
    public string Code { get; }
}
