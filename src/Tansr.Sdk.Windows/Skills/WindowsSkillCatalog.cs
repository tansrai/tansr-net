using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Skills;

/// <summary>
/// 显式白名单的技能资源装配器。不会发现 home/cwd、执行脚本、批准工具或生成系统提示词。
/// 所有磁盘读取复用 WindowsWorkspace 的句柄围栏；调用者继续负责权限与会话绑定。
/// </summary>
public sealed class WindowsSkillCatalog
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private readonly WindowsWorkspace? _workspace;
    private readonly Action<string, CancellationToken>? _authorize;
    private int _revoked;
    private readonly WindowsSkillDescriptor[] _descriptors;
    private readonly int _skillLimit, _resourceLimit, _assemblyLimit;
    private IReadOnlyList<WindowsSkillIndexEntry>? _index;

    public WindowsSkillCatalog(WindowsWorkspace workspace, IEnumerable<WindowsSkillDescriptor> descriptors,
        WindowsSkillCatalogOptions? options = null)
        : this(descriptors, options, workspace ?? throw new ArgumentNullException(nameof(workspace))) { }

    /// <summary>仅装配内联技能；目录技能仍要求显式 WindowsWorkspace。</summary>
    public static WindowsSkillCatalog FromInline(IEnumerable<WindowsSkillDescriptor> descriptors, WindowsSkillCatalogOptions? options = null)
        => new(descriptors, options, null);

    private WindowsSkillCatalog(IEnumerable<WindowsSkillDescriptor> descriptors, WindowsSkillCatalogOptions? options,
        WindowsWorkspace? workspace)
    {
        _workspace = workspace;
        if (descriptors == null) throw new ArgumentNullException(nameof(descriptors));
        options ??= new WindowsSkillCatalogOptions();
        _authorize = options.Authorize;
        if (options.MaximumSkills < 1 || options.MaximumSkills > 1024 || options.MaximumResourcesPerSkill < 0 ||
            options.MaximumResourcesPerSkill > 256 || options.MaximumSkillBytes < 1 || options.MaximumSkillBytes > 4 * 1024 * 1024 ||
            options.MaximumResourceBytes < 1 || options.MaximumResourceBytes > 4 * 1024 * 1024 ||
            options.MaximumAssemblyBytes < 1 || options.MaximumAssemblyBytes > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        _skillLimit = options.MaximumSkillBytes; _resourceLimit = options.MaximumResourceBytes; _assemblyLimit = options.MaximumAssemblyBytes;
        // Bounded enumeration also handles a caller that accidentally supplies an infinite enumerable.
        _descriptors = descriptors.Take(options.MaximumSkills + 1).ToArray();
        if (_descriptors.Length > options.MaximumSkills) throw new WindowsSkillException("size_limit");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _descriptors)
        {
            if (item == null || item.Name == null || !Regex.IsMatch(item.Name, "\\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\\z", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(item.Description) || Utf8.GetByteCount(item.Description) > 8192 ||
                (item.WhenToUse != null && Utf8.GetByteCount(item.WhenToUse) > 8192)) throw new WindowsSkillException("invalid_skill");
            if (!item.IsInline)
            {
                if (_workspace == null) throw new WindowsSkillException("workspace_required");
                ValidatePath(item.RelativePath);
                if (!files.Add(item.RelativePath)) throw new WindowsSkillException("duplicate_skill");
            }
            if (!names.Add(item.Name)) throw new WindowsSkillException("duplicate_skill");
            if (item.Resources.Count > options.MaximumResourcesPerSkill) throw new WindowsSkillException("size_limit");
            var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string parent = Parent(item.RelativePath);
            foreach (string path in item.Resources)
            {
                ValidatePath(path);
                if (!resources.Add(path) || string.Equals(path, item.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    !path.StartsWith(parent, StringComparison.Ordinal)) throw new WindowsSkillException("resource_not_allowed");
            }
        }
    }

    /// <summary>建立新冻结索引；失败时保留旧索引。正文不留驻索引，使用前必须重新读盘核对摘要。</summary>
    public IReadOnlyList<WindowsSkillIndexEntry> Index(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AssertTrusted(null, cancellationToken);
            var entries = new List<WindowsSkillIndexEntry>();
            foreach (var descriptor in _descriptors.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                AssertTrusted(descriptor.Name, cancellationToken);
                byte[] bytes = ReadBody(descriptor, cancellationToken);
                _ = Text(bytes); // Invalid UTF-8 is not silently rewritten before hashing or delivery.
                var resources = new List<WindowsSkillResourceIndexEntry>(); long total = bytes.Length;
                foreach (string path in descriptor.Resources.OrderBy(value => value, StringComparer.Ordinal))
                {
                    var resource = ReadBounded(path, _resourceLimit, cancellationToken);
                    total += resource.Length;
                    if (total > _assemblyLimit) throw new WindowsSkillException("size_limit");
                    resources.Add(new WindowsSkillResourceIndexEntry(path, Hash(resource), resource.Length));
                }
                if (total > _assemblyLimit) throw new WindowsSkillException("size_limit");
                string contentDigest = Hash(bytes);
                var immutable = new ReadOnlyCollection<WindowsSkillResourceIndexEntry>(resources);
                entries.Add(new WindowsSkillIndexEntry(descriptor, contentDigest, DefinitionDigest(descriptor, contentDigest, immutable), bytes.Length, immutable));
                AssertTrusted(descriptor.Name, cancellationToken);
            }
            _index = new ReadOnlyCollection<WindowsSkillIndexEntry>(entries);
            return _index;
        }
    }

    public WindowsSkillDocument Read(string name, string definitionDigest, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AssertTrusted(name, cancellationToken);
            var entry = Find(name, definitionDigest);
            var descriptor = _descriptors.First(value => value.Name == entry.Name);
            var bytes = ReadBody(descriptor, cancellationToken);
            Match(bytes, entry.ContentDigest);
            AssertTrusted(name, cancellationToken);
            return new WindowsSkillDocument(entry, Text(bytes));
        }
    }

    /// <summary>只读取冻结索引中的资源；不能通过正文链接、大小写别名或调用参数扩大白名单。</summary>
    public WindowsSkillResource ReadResource(string name, string definitionDigest, string relativePath,
        string expectedContentDigest, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AssertTrusted(name, cancellationToken);
            var skill = Find(name, definitionDigest);
            var entry = skill.Resources.FirstOrDefault(value => value.RelativePath == relativePath);
            if (entry == null) throw new WindowsSkillException("resource_not_allowed");
            if (entry.ContentDigest != expectedContentDigest) throw new WindowsSkillException("digest_mismatch");
            var bytes = ReadBounded(entry.RelativePath, _resourceLimit, cancellationToken);
            Match(bytes, entry.ContentDigest);
            AssertTrusted(name, cancellationToken);
            return new WindowsSkillResource(entry, bytes);
        }
    }

    /// <summary>返回不可变材料，不组织模型上下文。未指定资源时只装载正文；不会自动展开整个目录。</summary>
    public WindowsSkillAssembly Assemble(string name, string definitionDigest, IEnumerable<string>? resources = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AssertTrusted(name, cancellationToken);
            var entry = Find(name, definitionDigest);
            var requested = (resources ?? Array.Empty<string>()).Take(entry.Resources.Count + 1).ToArray();
            if (requested.Length > entry.Resources.Count || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
                throw new WindowsSkillException("resource_not_allowed");
            var document = Read(name, definitionDigest, cancellationToken);
            var loaded = new List<WindowsSkillResource>(); long total = entry.SizeBytes;
            foreach (string path in requested)
            {
                var resource = entry.Resources.FirstOrDefault(value => value.RelativePath == path);
                if (resource == null) throw new WindowsSkillException("resource_not_allowed");
                total += resource.SizeBytes;
                if (total > _assemblyLimit) throw new WindowsSkillException("size_limit");
                loaded.Add(ReadResource(name, definitionDigest, path, resource.ContentDigest, cancellationToken));
            }
            return new WindowsSkillAssembly(document, new ReadOnlyCollection<WindowsSkillResource>(loaded));
        }
    }

    /// <summary>立即永久撤回该目录实例；新授权应创建新实例及新索引，不复活旧摘要。</summary>
    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);

    private void AssertTrusted(string? name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _revoked) != 0) throw new WindowsSkillException("trust_revoked");
        if (name != null)
        {
            // Lookup permits case-insensitive aliases, but trust belongs to the registered
            // skill identity. An alias must not bypass a host's per-skill revocation.
            var registered = _descriptors.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));
            _authorize?.Invoke(registered?.Name ?? name, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _revoked) != 0) throw new WindowsSkillException("trust_revoked");
    }

    private byte[] ReadBody(WindowsSkillDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!descriptor.IsInline) return ReadBounded(descriptor.RelativePath, _skillLimit, cancellationToken);
        byte[] bytes;
        try { bytes = Utf8.GetBytes(descriptor.InlineContent!); }
        catch (EncoderFallbackException) { throw new WindowsSkillException("invalid_text_encoding"); }
        if (bytes.Length > _skillLimit) throw new WindowsSkillException("size_limit");
        return bytes;
    }

    private WindowsSkillIndexEntry Find(string name, string digest)
    {
        if (_index == null) throw new WindowsSkillException("not_indexed");
        var entry = _index.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new WindowsSkillException("skill_not_found");
        if (entry.DefinitionDigest != digest) throw new WindowsSkillException("digest_mismatch");
        return entry;
    }

    private byte[] ReadBounded(string path, int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_workspace == null) throw new WindowsSkillException("workspace_required");
        var before = _workspace.Inspect(path);
        if (before.Kind != WindowsWorkspaceEntryKind.File) throw new WindowsSkillException("invalid_resource_kind");
        if (before.Length > maximum) throw new WindowsSkillException("size_limit");
        // WindowsWorkspace retains one read handle with write/delete sharing disabled for the full read.
        // Inspect again catches growth at the boundary; digest comparison catches same-size replacement.
        byte[] bytes = _workspace.Read(path, 0, maximum, cancellationToken);
        var after = _workspace.Inspect(path);
        if (after.Kind != WindowsWorkspaceEntryKind.File || after.Length != bytes.LongLength || before.Length != bytes.LongLength ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc) throw new WindowsSkillException("resource_changed");
        return bytes;
    }

    private static string Text(byte[] bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new WindowsSkillException("invalid_text_encoding"); }
    }

    private static void Match(byte[] bytes, string digest)
    { if (Hash(bytes) != digest) throw new WindowsSkillException("resource_changed"); }

    private static string Hash(byte[] bytes)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private static string DefinitionDigest(WindowsSkillDescriptor descriptor, string contentDigest,
        IReadOnlyList<WindowsSkillResourceIndexEntry> resources)
    {
        using var stream = new MemoryStream();
        // Explicit version plus length-prefixed UTF-8 fields avoid delimiter ambiguity without defining a new wire API.
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write("tansr-windows-skill-v1"); writer.Write(descriptor.Name); writer.Write(descriptor.Description);
            writer.Write(descriptor.WhenToUse != null); if (descriptor.WhenToUse != null) writer.Write(descriptor.WhenToUse);
            writer.Write(descriptor.RelativePath); writer.Write(contentDigest); writer.Write(resources.Count);
            foreach (var resource in resources) { writer.Write(resource.RelativePath); writer.Write(resource.ContentDigest); }
        }
        return Hash(stream.ToArray());
    }

    private static string Parent(string path)
    { int slash = path.LastIndexOf('/'); return slash < 0 ? "" : path.Substring(0, slash + 1); }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 32767 || path.Any(value => value < 32 || value == 127) ||
            path.IndexOfAny(new[] { '\\', ':' }) >= 0) throw new WindowsSkillException("invalid_path");
        foreach (string part in path.Split('/'))
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                part.EndsWith(" ", StringComparison.Ordinal)) throw new WindowsSkillException("invalid_path");
        try { _ = Utf8.GetByteCount(path); }
        catch (EncoderFallbackException) { throw new WindowsSkillException("invalid_path"); }
    }
}
