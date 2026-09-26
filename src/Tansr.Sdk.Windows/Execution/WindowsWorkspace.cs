using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>
/// Windows 工作区资源后端。路径解析使用父目录句柄，文件操作不跟随重解析点。
/// 普通原子替换可用于共享目录；条件覆盖仅保证协作写者间的 CAS，不是同用户恶意进程沙箱。
/// 调用方仍须核验会话、能力、工作区代际、批准和持久执行账本。
/// </summary>
public sealed class WindowsWorkspace : IDisposable
{
    private const string ReservedPrefix = ".tansr-sdk-";
    private readonly object _gate = new object();
    private readonly List<SafeFileHandle> _anchors = new List<SafeFileHandle>();
    private readonly int _maximumReadBytes, _maximumWriteBytes, _maximumEntries;
    private readonly bool _cooperative;
    private SafeFileHandle? _writerLock;
    private bool _disposed;
    private readonly string _rootFinalPath;

    public WindowsWorkspace(string rootDirectory, WindowsWorkspaceOptions? options = null)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Windows workspace requires Windows.");
        options ??= new WindowsWorkspaceOptions();
        if (options.MaximumReadBytes < 1 || options.MaximumReadBytes > 64 * 1024 * 1024 ||
            options.MaximumWriteBytes < 1 || options.MaximumWriteBytes > 64 * 1024 * 1024 ||
            options.MaximumEntries < 1 || options.MaximumEntries > 100000)
            throw new ArgumentOutOfRangeException(nameof(options));
        RootDirectory = ValidateRoot(rootDirectory);
        _maximumReadBytes = options.MaximumReadBytes; _maximumWriteBytes = options.MaximumWriteBytes; _maximumEntries = options.MaximumEntries;
        _cooperative = options.AllWritersCooperate;
        try
        {
            _anchors.Add(NativeWorkspace.OpenDrive(RootDirectory.Substring(0, 3)));
            foreach (string part in RootDirectory.Substring(3).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                _anchors.Add(NativeWorkspace.OpenDirectory(_anchors[_anchors.Count - 1], part));
            _rootFinalPath = NativeWorkspace.FinalPath(RootHandle).TrimEnd('\\');
            if (_cooperative)
            {
                NativeWorkspace.ValidatePrivatePermissions(RootHandle);
                try { _writerLock = NativeWorkspace.OpenFile(RootHandle, ReservedPrefix + "writer.lock", true, true); }
                catch (WindowsWorkspaceException exception) when (exception.Code == "already_exists")
                { _writerLock = NativeWorkspace.OpenFile(RootHandle, ReservedPrefix + "writer.lock", true); }
                NativeWorkspace.ValidatePrivatePermissions(_writerLock);
            }
        }
        catch { DisposeHandles(_anchors); _writerLock?.Dispose(); throw; }
    }

    public string RootDirectory { get; }
    public bool SupportsAtomicReplacement => true;
    /// <summary>文件数据已 flush；未承诺断电后目录项提交顺序，因此不能据此签发档案耐久 ACK。</summary>
    public bool GuaranteesPowerLossDurability => false;
    public bool SupportsCooperativeCompareExchange => _cooperative;
    public bool SupportsUncoordinatedCompareExchange => false;
    private SafeFileHandle RootHandle => _anchors[_anchors.Count - 1];

    public WindowsWorkspaceEntry Inspect(string relativePath)
    {
        lock (_gate)
        {
            Check(); string[] parts = Parts(relativePath);
            if (parts.Length == 0) return Entry("", NativeWorkspace.Validate(RootHandle, true));
            using var parent = HoldParent(parts);
            // inspect 不枚举整个父目录；大目录中的单文件读取不受枚举帽牵连。
            using var handle = NativeWorkspace.OpenMetadata(parent.Last, parts[parts.Length - 1]);
            var information = NativeWorkspace.Information(handle);
            if ((information.Attributes & NativeWorkspace.ReparseAttribute) != 0)
                return new WindowsWorkspaceEntry(relativePath, WindowsWorkspaceEntryKind.ReparsePoint, information.Length, information.WriteTime);
            return Entry(relativePath, information);
        }
    }

    public byte[] Read(string relativePath, long offset = 0, int? maximumBytes = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Check(); cancellationToken.ThrowIfCancellationRequested();
            int maximum = maximumBytes ?? _maximumReadBytes;
            if (offset < 0 || maximum < 0 || maximum > _maximumReadBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            string[] parts = FileParts(relativePath);
            using var parent = HoldParent(parts);
            using var handle = NativeWorkspace.OpenFile(parent.Last, parts[parts.Length - 1]);
            return ReadHandle(handle, offset, maximum, false, cancellationToken);
        }
    }

    public IReadOnlyList<WindowsWorkspaceEntry> List(string relativeDirectory = "")
    {
        lock (_gate)
        {
            Check(); using var directory = HoldDirectory(Parts(relativeDirectory));
            return ListHeld(directory.Last, relativeDirectory);
        }
    }

    public void CreateDirectory(string relativeDirectory)
    {
        lock (_gate)
        {
            Check(); string[] parts = FileParts(relativeDirectory);
            using var parent = HoldParent(parts);
            using var created = NativeWorkspace.OpenDirectory(parent.Last, parts[parts.Length - 1], true);
        }
    }

    /// <summary>原子替换指定目录项；不会读取或跟随目标符号链接。不是带条件的提交。</summary>
    public WindowsWorkspaceWriteResult WriteAtomic(string relativePath, byte[] bytes, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        lock (_gate) { Check(); return WriteCore(relativePath, bytes, overwrite, false, null, cancellationToken); }
    }

    /// <summary>目标已经存在则失败；创建不使用先检查后写入。</summary>
    public WindowsWorkspaceWriteResult CreateFileAtomic(string relativePath, byte[] bytes, CancellationToken cancellationToken = default)
        => WriteAtomic(relativePath, bytes, false, cancellationToken);

    /// <summary>
    /// expectedHash=null 表示必须不存在。覆盖 CAS 仅在可信宿主保证所有写者协作时可用。
    /// 非协作编辑器或同用户进程可绕过写者锁，不能对这些写者宣告原子 CAS。
    /// </summary>
    public WindowsWorkspaceWriteResult CompareExchange(string relativePath, string? expectedHash, byte[] bytes, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Check(); ValidateHash(expectedHash);
            if (expectedHash != null && !_cooperative) throw new WindowsWorkspaceException("compare_exchange_unavailable");
            return WriteCore(relativePath, bytes, expectedHash != null, true, expectedHash, cancellationToken);
        }
    }

    /// <summary>精确单次文本替换。只允许协作写者域，旧正文不唯一或版本变化时拒绝。</summary>
    public WindowsWorkspaceWriteResult EditText(string relativePath, string oldText, string newText, string? expectedHash = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(oldText)) throw new ArgumentException("Old text must not be empty.", nameof(oldText));
        if (newText == null) throw new ArgumentNullException(nameof(newText));
        lock (_gate)
        {
            Check(); if (!_cooperative) throw new WindowsWorkspaceException("compare_exchange_unavailable");
            ValidateHash(expectedHash);
            string[] parts = FileParts(relativePath);
            using var parent = HoldParent(parts);
            byte[] original;
            using (var handle = NativeWorkspace.OpenFile(parent.Last, parts[parts.Length - 1]))
                original = ReadHandle(handle, 0, _maximumReadBytes, true, cancellationToken);
            string originalHash = Hash(original);
            if (expectedHash != null && originalHash != expectedHash) throw new WindowsWorkspaceException("write_conflict");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(original); }
            catch (DecoderFallbackException) { throw new WindowsWorkspaceException("invalid_text_encoding"); }
            int at = text.IndexOf(oldText, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(oldText, at + oldText.Length, StringComparison.Ordinal) >= 0) throw new WindowsWorkspaceException("edit_not_unique");
            byte[] changed = new UTF8Encoding(false, true).GetBytes(text.Substring(0, at) + newText + text.Substring(at + oldText.Length));
            return WriteCore(relativePath, changed, true, true, originalHash, cancellationToken);
        }
    }

    /// <summary>有界路径匹配，仅提供文件资源事实，不执行智能体搜索算法。</summary>
    public IReadOnlyList<string> Glob(string pattern, CancellationToken cancellationToken = default)
    {
        var matcher = GlobMatcher(pattern);
        lock (_gate)
        {
            Check(); var found = new List<string>();
            Walk((path, item) => { if (matcher.IsMatch(path)) found.Add(path); }, cancellationToken);
            return found;
        }
    }

    /// <summary>按行文本检索；不跟链接，限制文件数、单文件读取、匹配数及正则耗时。</summary>
    public WindowsWorkspaceSearchResult Grep(string query, string glob = "**/*", bool regularExpression = false, int maximumMatches = 200, CancellationToken cancellationToken = default)
    {
        if (query == null || query.Length == 0 || query.Length > 4096 || maximumMatches < 1 || maximumMatches > 10000) throw new ArgumentOutOfRangeException(nameof(query));
        var matcher = GlobMatcher(glob);
        Regex? expression = regularExpression ? new Regex(query, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : null;
        lock (_gate)
        {
            Check(); var found = new List<WindowsWorkspaceSearchMatch>(); bool truncated = false;
            Walk((path, item) =>
            {
                if (item.Kind != WindowsWorkspaceEntryKind.File || !matcher.IsMatch(path)) return;
                if (found.Count == maximumMatches) { truncated = true; return; }
                if (item.Length > _maximumReadBytes) { truncated = true; return; }
                string text;
                try { text = new UTF8Encoding(false, true).GetString(Read(path, 0, _maximumReadBytes, cancellationToken)); }
                catch (DecoderFallbackException) { return; }
                if (text.IndexOf('\0') >= 0) return;
                using var reader = new StringReader(text); int number = 0; string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested(); number++;
                    if (expression != null ? expression.IsMatch(line) : line.IndexOf(query, StringComparison.Ordinal) >= 0)
                    {
                        if (found.Count == maximumMatches) { truncated = true; break; }
                        found.Add(new WindowsWorkspaceSearchMatch(path, number, line.Length <= 4096 ? line : line.Substring(0, 4096)));
                    }
                }
            }, cancellationToken);
            return new WindowsWorkspaceSearchResult(found, truncated);
        }
    }

    /// <summary>固定 cwd 及其所有父目录；不能限制已批准进程访问 cwd 以外的路径。</summary>
    public IWindowsProcessWorkingDirectoryLease AcquireProcessDirectory(string relativeDirectory = "")
    {
        lock (_gate)
        {
            Check(); string[] relative = Parts(relativeDirectory);
            var handles = new List<SafeFileHandle>();
            SafeFileHandle? guard = null;
            try
            {
                // 不共享 DELETE 固定每级身份；共享 WRITE，不能因一个子进程禁止整盘普通文件原子保存。
                handles.Add(NativeWorkspace.OpenDrive(RootDirectory.Substring(0, 3)));
                var parts = RootDirectory.Substring(3).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Concat(relative).ToArray();
                foreach (string part in parts) handles.Add(NativeWorkspace.OpenDirectory(handles[handles.Count - 1], part));
                try
                {
                    // 下一子项被固定使祖先持续非空；末目录也须如此，防止运行期转成 junction。
                    // 对本次新建文件使用原句柄 delete-on-close，绝不清扫未知文件或按可替换路径删除。
                    guard = NativeWorkspace.OpenFile(handles[handles.Count - 1], ReservedPrefix + "process-" + Guid.NewGuid().ToString("N") + ".lock",
                        write: true, create: true, deleteOnClose: true);
                }
                catch (WindowsWorkspaceException error) when (error.Code == "access_denied" || error.Code == "native_error_19")
                {
                    // 只读工作区保留原可运行性，只对末目录使用原拒写保护，不锁它的祖先。
                    handles.Add(parts.Length == 0 ? NativeWorkspace.OpenDrive(RootDirectory.Substring(0, 3), true) :
                        NativeWorkspace.OpenDirectory(handles[handles.Count - 2], parts[parts.Length - 1], false, true));
                }
                string final = NativeWorkspace.FinalPath(handles[handles.Count - 1]);
                if (!IsWithinRoot(final)) throw new WindowsWorkspaceException("path_identity_changed");
                var lease = new ProcessDirectoryLease(handles, guard, final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final.Substring(4) : final);
                lease.ValidateForExecution(); return lease;
            }
            catch { guard?.Dispose(); DisposeHandles(handles); throw; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _writerLock?.Dispose(); DisposeHandles(_anchors);
        }
    }

    private WindowsWorkspaceWriteResult WriteCore(string relativePath, byte[] bytes, bool overwrite, bool conditional, string? expectedHash, CancellationToken cancellationToken)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length > _maximumWriteBytes) throw new WindowsWorkspaceException("write_limit");
        // 复制后再哈希/写入，调用者在另一线程修改数组不能制造结果摘要与磁盘不一致。
        byte[] data = (byte[])bytes.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        string[] parts = FileParts(relativePath); using var parent = HoldParent(parts);
        string name = parts[parts.Length - 1];
        if (overwrite)
        {
            try
            {
                using var original = NativeWorkspace.OpenFile(parent.Last, name);
                if (_cooperative) NativeWorkspace.ValidatePrivatePermissions(original);
                if (conditional && Hash(ReadHandle(original, 0, _maximumReadBytes, true, cancellationToken)) != expectedHash)
                    throw new WindowsWorkspaceException("write_conflict");
            }
            catch (WindowsWorkspaceException exception) when (exception.Code == "not_found" && !conditional) { }
        }
        string tempName = ReservedPrefix + Guid.NewGuid().ToString("N") + ".tmp";
        using var temp = NativeWorkspace.OpenFile(parent.Last, tempName, true, true);
        bool committed = false;
        try
        {
            if (_cooperative) NativeWorkspace.ValidatePrivatePermissions(temp);
            // FileStream关闭时不拥有原句柄；真正的句柄由当前操作统一释放。
            using (var stream = BorrowedStream(temp, FileAccess.Write))
            { stream.Write(data, 0, data.Length); stream.Flush(); }
            NativeWorkspace.Flush(temp);
            cancellationToken.ThrowIfCancellationRequested();
            NativeWorkspace.Validate(parent.Last, true);
            NativeWorkspace.Rename(temp, parent.Last, name, overwrite);
            committed = true;
            // 原子名称切换已成功。此后不再用取消覆盖已发生的副作用事实。
            return new WindowsWorkspaceWriteResult(Hash(data), data.LongLength);
        }
        finally
        {
            if (!committed) NativeWorkspace.DeleteOpenedFile(temp);
        }
    }

    private byte[] ReadHandle(SafeFileHandle handle, long offset, int maximum, bool requireWhole, CancellationToken cancellationToken)
    {
        var before = NativeWorkspace.Validate(handle, false);
        if (requireWhole && before.Length > maximum) throw new WindowsWorkspaceException("read_limit");
        long length = Math.Min(Math.Max(0, before.Length - offset), maximum);
        byte[] result = new byte[(int)length];
        using (var stream = BorrowedStream(handle, FileAccess.Read))
        {
            stream.Position = offset;
            int read = 0;
            while (read < result.Length)
            {
                cancellationToken.ThrowIfCancellationRequested(); int count = stream.Read(result, read, Math.Min(result.Length - read, 65536));
                if (count == 0) throw new WindowsWorkspaceException("file_changed"); read += count;
            }
        }
        var after = NativeWorkspace.Validate(handle, false);
        if (before.Length != after.Length || before.WriteTime != after.WriteTime) throw new WindowsWorkspaceException("file_changed");
        return result;
    }

    private static FileStream BorrowedStream(SafeFileHandle handle, FileAccess access)
        => new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), false), access, 4096, false);

    private IReadOnlyList<WindowsWorkspaceEntry> ListHeld(SafeFileHandle handle, string directory)
    {
        return NativeWorkspace.Enumerate(handle, _maximumEntries).Where(item => !item.Name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => new WindowsWorkspaceEntry(Join(directory, item.Name), (item.Attributes & NativeWorkspace.ReparseAttribute) != 0 ? WindowsWorkspaceEntryKind.ReparsePoint :
                (item.Attributes & NativeWorkspace.DirectoryAttribute) != 0 ? WindowsWorkspaceEntryKind.Directory : WindowsWorkspaceEntryKind.File, item.Length, item.WriteTime))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void Walk(Action<string, WindowsWorkspaceEntry> visit, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(); pending.Push(""); int scanned = 0;
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); string directory = pending.Pop();
            foreach (var item in List(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > _maximumEntries) throw new WindowsWorkspaceException("entry_limit");
                Parts(item.RelativePath);
                if (item.Kind == WindowsWorkspaceEntryKind.ReparsePoint) continue;
                visit(item.RelativePath, item);
                if (item.Kind == WindowsWorkspaceEntryKind.Directory) pending.Push(item.RelativePath);
            }
        }
    }

    private HandleChain HoldParent(string[] parts) => HoldDirectory(parts.Take(parts.Length - 1).ToArray());
    private HandleChain HoldDirectory(string[] parts)
    {
        var chain = new HandleChain(RootHandle);
        try
        {
            NativeWorkspace.Validate(RootHandle, true);
            if (_cooperative) NativeWorkspace.ValidatePrivatePermissions(RootHandle);
            foreach (string part in parts)
            {
                chain.Add(NativeWorkspace.OpenDirectory(chain.Last, part));
                if (_cooperative) NativeWorkspace.ValidatePrivatePermissions(chain.Last);
            }
            if (!IsWithinRoot(NativeWorkspace.FinalPath(chain.Last))) throw new WindowsWorkspaceException("path_identity_changed");
            return chain;
        }
        catch { chain.Dispose(); throw; }
    }

    private bool IsWithinRoot(string path) => string.Equals(path.TrimEnd('\\'), _rootFinalPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(_rootFinalPath + "\\", StringComparison.OrdinalIgnoreCase);
    private void Check() { if (_disposed) throw new ObjectDisposedException(nameof(WindowsWorkspace)); }
    private static WindowsWorkspaceEntry Entry(string path, NativeWorkspace.FileInformation info)
        => new WindowsWorkspaceEntry(path, (info.Attributes & NativeWorkspace.DirectoryAttribute) != 0 ? WindowsWorkspaceEntryKind.Directory : WindowsWorkspaceEntryKind.File, info.Length, info.WriteTime);
    private static string Join(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;
    private static string Hash(byte[] bytes) { using var algorithm = SHA256.Create(); return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static void ValidateHash(string? hash) { if (hash != null && !Regex.IsMatch(hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Expected hash must be a lowercase SHA256.", nameof(hash)); }

    private static string ValidateRoot(string root)
    {
        if (root == null || root.Length < 3 || root.Length > 30000 || !((root[0] >= 'A' && root[0] <= 'Z') || (root[0] >= 'a' && root[0] <= 'z')) || root[1] != ':' || root[2] != '\\')
            throw new WindowsWorkspaceException("unsafe_path");
        string normalized = root.TrimEnd('\\'); if (normalized.Length == 2) normalized += "\\";
        if (normalized.Length > 3) foreach (string part in normalized.Substring(3).Split('\\')) ValidatePart(part);
        return normalized;
    }

    private static string[] Parts(string relative)
    {
        if (relative == null || relative.Length > 4096) throw new WindowsWorkspaceException("unsafe_path");
        if (relative.Length == 0) return Array.Empty<string>();
        string[] parts = relative.Split('/'); if (parts.Length > 64) throw new WindowsWorkspaceException("path_limit");
        foreach (string part in parts) ValidatePart(part);
        return parts;
    }

    private static string[] FileParts(string relative)
    { string[] parts = Parts(relative); if (parts.Length == 0) throw new WindowsWorkspaceException("unsafe_path"); return parts; }

    private static void ValidatePart(string part)
    {
        if (part.Length == 0 || part.Length > 255 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
            part.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase) || part.Any(value => value < 32 || value == 127 || "\\/:*?\"<>|".IndexOf(value) >= 0) ||
            Regex.IsMatch(part, @"^(?:con|prn|aux|nul|conin\$|conout\$|com[0-9¹²³]|lpt[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new WindowsWorkspaceException("unsafe_path");
        for (int index = 0; index < part.Length; index++)
        {
            if (char.IsHighSurrogate(part[index]))
            {
                if (index + 1 >= part.Length || !char.IsLowSurrogate(part[index + 1])) throw new WindowsWorkspaceException("unsafe_path");
                index++;
            }
            else if (char.IsLowSurrogate(part[index])) throw new WindowsWorkspaceException("unsafe_path");
        }
    }

    private static Regex GlobMatcher(string pattern)
    {
        if (pattern == null || pattern.Length == 0 || pattern.Length > 4096 || pattern[0] == '/' || pattern.IndexOf('\\') >= 0 || pattern.IndexOf(':') >= 0 || pattern.Split('/').Any(part => part == ".." || part == "." || part.Length == 0))
            throw new ArgumentException("Glob must be workspace-relative.", nameof(pattern));
        var regex = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char value = pattern[i];
            if (value == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            { i++; if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; regex.Append("(?:.*/)?"); } else regex.Append(".*"); }
            else if (value == '*') regex.Append("[^/]*");
            else if (value == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(value.ToString()));
        }
        return new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static void DisposeHandles(IList<SafeFileHandle> handles) { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    private sealed class HandleChain : IDisposable
    {
        private readonly List<SafeFileHandle> _owned = new List<SafeFileHandle>(); private readonly SafeFileHandle _root;
        internal HandleChain(SafeFileHandle root) { _root = root; }
        internal SafeFileHandle Last => _owned.Count == 0 ? _root : _owned[_owned.Count - 1];
        internal void Add(SafeFileHandle handle) => _owned.Add(handle);
        public void Dispose() => DisposeHandles(_owned);
    }

    private sealed class ProcessDirectoryLease : IWindowsProcessWorkingDirectoryLease
    {
        private readonly List<SafeFileHandle> _handles; private readonly SafeFileHandle? _guard; private readonly string _finalPath; private bool _disposed;
        internal ProcessDirectoryLease(List<SafeFileHandle> handles, SafeFileHandle? guard, string path) { _handles = handles; _guard = guard; DirectoryPath = path; _finalPath = NativeWorkspace.FinalPath(handles[handles.Count - 1]); }
        public string DirectoryPath { get; }
        public void ValidateForExecution()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProcessDirectoryLease));
            foreach (var handle in _handles) NativeWorkspace.Validate(handle, true);
            if (_guard != null) NativeWorkspace.Validate(_guard, false);
            if (!string.Equals(_finalPath, NativeWorkspace.FinalPath(_handles[_handles.Count - 1]), StringComparison.OrdinalIgnoreCase)) throw new WindowsWorkspaceException("path_identity_changed");
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _guard?.Dispose(); DisposeHandles(_handles); }
    }
}
