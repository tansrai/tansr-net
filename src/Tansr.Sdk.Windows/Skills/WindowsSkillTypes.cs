using System.Collections.ObjectModel;

namespace Tansr.Sdk.Windows.Skills;

/// <summary>可信宿主明确列出的技能；描述和正文仅作为资源，不授予执行权限。</summary>
public sealed class WindowsSkillDescriptor
{
    public WindowsSkillDescriptor(string name, string description, string relativePath,
        string? whenToUse = null, IEnumerable<string>? resources = null)
    {
        Name = name; Description = description; RelativePath = relativePath; WhenToUse = whenToUse;
        var paths = (resources ?? Array.Empty<string>()).Take(257).ToArray();
        if (paths.Length > 256) throw new WindowsSkillException("size_limit");
        Resources = new ReadOnlyCollection<string>(paths);
    }
    public string Name { get; }
    public string Description { get; }
    public string RelativePath { get; }
    public string? WhenToUse { get; }
    /// <summary>工作区内的完整相对路径；每项必须位于该技能目录下，不自动读取正文中的链接。</summary>
    public IReadOnlyList<string> Resources { get; }
}

public sealed class WindowsSkillCatalogOptions
{
    public int MaximumSkills { get; set; } = 128;
    public int MaximumResourcesPerSkill { get; set; } = 64;
    public int MaximumSkillBytes { get; set; } = 256 * 1024;
    public int MaximumResourceBytes { get; set; } = 1024 * 1024;
    public int MaximumAssemblyBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class WindowsSkillResourceIndexEntry
{
    internal WindowsSkillResourceIndexEntry(string path, string digest, int size)
    { RelativePath = path; ContentDigest = digest; SizeBytes = size; }
    public string RelativePath { get; }
    public string ContentDigest { get; }
    public int SizeBytes { get; }
}

public sealed class WindowsSkillIndexEntry
{
    internal WindowsSkillIndexEntry(WindowsSkillDescriptor descriptor, string contentDigest, string definitionDigest,
        int size, IReadOnlyList<WindowsSkillResourceIndexEntry> resources)
    {
        Name = descriptor.Name; Description = descriptor.Description; RelativePath = descriptor.RelativePath;
        WhenToUse = descriptor.WhenToUse; ContentDigest = contentDigest; DefinitionDigest = definitionDigest;
        SizeBytes = size; Resources = resources;
    }
    public string Name { get; }
    public string Description { get; }
    public string? WhenToUse { get; }
    public string RelativePath { get; }
    public string ContentDigest { get; }
    /// <summary>本地目录装配摘要，绑定元数据、正文摘要及资源白名单；不是 Serve 工具定义摘要。</summary>
    public string DefinitionDigest { get; }
    public int SizeBytes { get; }
    public IReadOnlyList<WindowsSkillResourceIndexEntry> Resources { get; }
}

public sealed class WindowsSkillDocument
{
    internal WindowsSkillDocument(WindowsSkillIndexEntry entry, string content) { Entry = entry; Content = content; }
    public WindowsSkillIndexEntry Entry { get; }
    /// <summary>完整 SKILL.md，包括原 frontmatter；由 Serve/kernel 决定解析和上下文采用方式。</summary>
    public string Content { get; }
}

public sealed class WindowsSkillResource
{
    internal WindowsSkillResource(WindowsSkillResourceIndexEntry entry, byte[] bytes)
    { Entry = entry; Bytes = new ReadOnlyCollection<byte>(bytes); }
    public WindowsSkillResourceIndexEntry Entry { get; }
    public IReadOnlyList<byte> Bytes { get; }
}

public sealed class WindowsSkillAssembly
{
    internal WindowsSkillAssembly(WindowsSkillDocument document, IReadOnlyList<WindowsSkillResource> resources)
    { Document = document; Resources = resources; }
    public WindowsSkillDocument Document { get; }
    public IReadOnlyList<WindowsSkillResource> Resources { get; }
}

public sealed class WindowsSkillException : IOException
{
    public WindowsSkillException(string code) : base("Windows skill resource operation failed: " + code) { Code = code; }
    public string Code { get; }
}
