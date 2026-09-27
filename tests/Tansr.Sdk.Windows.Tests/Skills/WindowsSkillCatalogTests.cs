using System.Runtime.InteropServices;
using System.Text;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Skills;

namespace Tansr.Sdk.Windows.Tests.Skills;

public sealed class WindowsSkillCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-skills-test-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace _workspace;
    public WindowsSkillCatalogTests()
    {
        Directory.CreateDirectory(_directory); _workspace = new WindowsWorkspace(_directory);
        _workspace.CreateDirectory("skills"); _workspace.CreateDirectory("skills/review");
        Write("skills/review/SKILL.md", "---\nname: review\ndescription: Review contracts\n---\n\n审阅指导；请调用工具而非直接运行脚本。");
        Write("skills/review/reference.md", "合同参考");
    }

    private void Write(string path, string text) => _workspace.WriteAtomic(path, Encoding.UTF8.GetBytes(text));
    private WindowsSkillDescriptor Descriptor(string description = "Review contracts")
        => new("review", description, "skills/review/SKILL.md", "合同审查", new[] { "skills/review/reference.md" });
    private WindowsSkillCatalog Catalog(WindowsSkillCatalogOptions? options = null) => new(_workspace, new[] { Descriptor() }, options);

    [Fact]
    public void IndexReadAndAssemblyUseActualFilesWithoutExecutingInstructions()
    {
        var catalog = Catalog(); var index = Assert.Single(catalog.Index());
        Assert.Equal("review", index.Name); Assert.Equal("合同审查", index.WhenToUse);
        Assert.Equal(64, index.ContentDigest.Length); Assert.Equal(64, index.DefinitionDigest.Length);
        Assert.Equal(index.DefinitionDigest, Assert.Single(catalog.Index()).DefinitionDigest);
        var body = catalog.Read("REVIEW", index.DefinitionDigest);
        Assert.StartsWith("---\n", body.Content); Assert.Contains("审阅指导", body.Content);
        Assert.Empty(catalog.Assemble("review", index.DefinitionDigest).Resources);
        var assembly = catalog.Assemble("review", index.DefinitionDigest, new[] { "skills/review/reference.md" });
        var resource = Assert.Single(assembly.Resources);
        Assert.Equal("合同参考", Encoding.UTF8.GetString(resource.Bytes.ToArray()));
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)resource.Bytes)[0] = 0);
        Assert.Throws<NotSupportedException>(() => ((IList<WindowsSkillIndexEntry>)catalog.Index()).Clear());
    }

    [Fact]
    public void EmptyCatalogDoesNotDiscoverOtherFilesOrUserHome()
    {
        var catalog = new WindowsSkillCatalog(_workspace, Array.Empty<WindowsSkillDescriptor>());
        Assert.Empty(catalog.Index());
        Assert.Equal("skill_not_found", Assert.Throws<WindowsSkillException>(() => catalog.Read("review", new string('a', 64))).Code);
    }

    [Fact]
    public void BodyMutationRequiresExplicitNewIndexAndDigest()
    {
        var catalog = Catalog(); var old = Assert.Single(catalog.Index());
        Write("skills/review/SKILL.md", "Mutated body");
        Assert.Equal("resource_changed", Assert.Throws<WindowsSkillException>(() => catalog.Read("review", old.DefinitionDigest)).Code);
        var current = Assert.Single(catalog.Index()); Assert.NotEqual(old.DefinitionDigest, current.DefinitionDigest);
        Assert.Equal("digest_mismatch", Assert.Throws<WindowsSkillException>(() => catalog.Read("review", old.DefinitionDigest)).Code);
        Assert.Equal("Mutated body", catalog.Read("review", current.DefinitionDigest).Content);
    }

    [Fact]
    public void SameSizeResourceMutationCannotBypassDigest()
    {
        var catalog = Catalog(); var index = Assert.Single(catalog.Index()); var resource = Assert.Single(index.Resources);
        var path = Path.Combine(_directory, "skills", "review", "reference.md"); var time = File.GetLastWriteTimeUtc(path);
        Write(resource.RelativePath, "恶意替换"); File.SetLastWriteTimeUtc(path, time);
        Assert.Equal("resource_changed", Assert.Throws<WindowsSkillException>(() =>
            catalog.ReadResource("review", index.DefinitionDigest, resource.RelativePath, resource.ContentDigest)).Code);
        Assert.Equal("resource_changed", Assert.Throws<WindowsSkillException>(() =>
            catalog.Assemble("review", index.DefinitionDigest, new[] { resource.RelativePath })).Code);
    }

    [Fact]
    public void DescriptionAndResourceWhiteListAreDigestBound()
    {
        var first = Assert.Single(Catalog().Index());
        var changedDescription = Assert.Single(new WindowsSkillCatalog(_workspace, new[] { Descriptor("different") }).Index());
        var changedResources = Assert.Single(new WindowsSkillCatalog(_workspace,
            new[] { new WindowsSkillDescriptor("review", "Review contracts", "skills/review/SKILL.md", "合同审查") }).Index());
        Assert.Equal(first.ContentDigest, changedDescription.ContentDigest);
        Assert.NotEqual(first.DefinitionDigest, changedDescription.DefinitionDigest);
        Assert.NotEqual(first.DefinitionDigest, changedResources.DefinitionDigest);
    }

    [Fact]
    public void UnlistedResourcesAndDigestMismatchRejectWithoutFollowingBodyLinks()
    {
        Write("secret.txt", "synthetic secret");
        var catalog = Catalog(); var index = Assert.Single(catalog.Index()); var resource = Assert.Single(index.Resources);
        Assert.Equal("resource_not_allowed", Assert.Throws<WindowsSkillException>(() =>
            catalog.ReadResource("review", index.DefinitionDigest, "secret.txt", resource.ContentDigest)).Code);
        Assert.Equal("resource_not_allowed", Assert.Throws<WindowsSkillException>(() =>
            catalog.ReadResource("review", index.DefinitionDigest, resource.RelativePath.ToUpperInvariant(), resource.ContentDigest)).Code);
        Assert.Equal("digest_mismatch", Assert.Throws<WindowsSkillException>(() =>
            catalog.ReadResource("review", index.DefinitionDigest, resource.RelativePath, new string('0', 64))).Code);
        Assert.Equal("resource_not_allowed", Assert.Throws<WindowsSkillException>(() =>
            catalog.Assemble("review", index.DefinitionDigest, new[] { resource.RelativePath, resource.RelativePath })).Code);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("skills\\review\\SKILL.md")]
    [InlineData("skills//SKILL.md")]
    [InlineData("skills/review/./SKILL.md")]
    [InlineData("skills/review./SKILL.md")]
    [InlineData("skills/review/SKILL.md ")]
    public void NonCanonicalPathsAreRejectedAtRegistration(string path)
    {
        Assert.Equal("invalid_path", Assert.Throws<WindowsSkillException>(() => new WindowsSkillCatalog(_workspace,
            new[] { new WindowsSkillDescriptor("review", "desc", path) })).Code);
    }

    [Fact]
    public void ResourcesCannotEscapeSkillDirectoryByPrefixOrCase()
    {
        foreach (var resource in new[] { "skills/review-other/secret", "skills/Review/secret", "secret.txt", "skills/review/SKILL.md" })
            Assert.Equal("resource_not_allowed", Assert.Throws<WindowsSkillException>(() => new WindowsSkillCatalog(_workspace,
                new[] { new WindowsSkillDescriptor("review", "desc", "skills/review/SKILL.md", resources: new[] { resource }) })).Code);
    }

    [Fact]
    public void DuplicateNamesAndWindowsPathAliasesAreRejected()
    {
        Assert.Equal("duplicate_skill", Assert.Throws<WindowsSkillException>(() => new WindowsSkillCatalog(_workspace,
            new[] { Descriptor(), new WindowsSkillDescriptor("REVIEW", "desc", "skills/other/SKILL.md") })).Code);
        Assert.Equal("duplicate_skill", Assert.Throws<WindowsSkillException>(() => new WindowsSkillCatalog(_workspace,
            new[] { Descriptor(), new WindowsSkillDescriptor("other", "desc", "SKILLS/REVIEW/SKILL.MD") })).Code);
    }

    [Fact]
    public void ByteLimitsAndCancellationDoNotReplaceValidIndex()
    {
        var catalog = Catalog(new WindowsSkillCatalogOptions { MaximumResourceBytes = 20 });
        var old = Assert.Single(catalog.Index());
        Write("skills/review/reference.md", new string('x', 21));
        Assert.Equal("size_limit", Assert.Throws<WindowsSkillException>(() => catalog.Index()).Code);
        Assert.Contains("审阅指导", catalog.Read("review", old.DefinitionDigest).Content);
        Assert.Throws<OperationCanceledException>(() => catalog.Index(new CancellationToken(true)));
        Assert.Equal("size_limit", Assert.Throws<WindowsSkillException>(() => Catalog(
            new WindowsSkillCatalogOptions { MaximumAssemblyBytes = 2 }).Index()).Code);
    }

    [Fact]
    public void BinaryBodyAndDirectoryBodyAreNotSilentlyAccepted()
    {
        _workspace.WriteAtomic("skills/review/SKILL.md", new byte[] { 0xff, 0xfe, 0x00 });
        Assert.Equal("invalid_text_encoding", Assert.Throws<WindowsSkillException>(() => Catalog().Index()).Code);
        var directory = new WindowsSkillCatalog(_workspace, new[] { new WindowsSkillDescriptor("dir", "desc", "skills/review") });
        Assert.Equal("invalid_resource_kind", Assert.Throws<WindowsSkillException>(() => directory.Index()).Code);
    }

    [Fact]
    public void HardLinkedResourcesAreRejectedByTheSharedWorkspaceFence()
    {
        Write("unlisted-secret.txt", "synthetic secret");
        var linked = Path.Combine(_directory, "skills", "review", "linked.md");
        Assert.True(CreateHardLink(linked, Path.Combine(_directory, "unlisted-secret.txt"), IntPtr.Zero));
        var catalog = new WindowsSkillCatalog(_workspace, new[] {
            new WindowsSkillDescriptor("review", "desc", "skills/review/SKILL.md", resources: new[] { "skills/review/linked.md" }) });
        Assert.Throws<WindowsWorkspaceException>(() => catalog.Index());
        Assert.Equal("synthetic secret", File.ReadAllText(Path.Combine(_directory, "unlisted-secret.txt")));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);

    [Fact]
    public void IndexIsRequiredAndRegistrationCopiesResourceList()
    {
        var resources = new[] { "skills/review/reference.md" };
        var descriptor = new WindowsSkillDescriptor("review", "desc", "skills/review/SKILL.md", resources: resources);
        resources[0] = "outside.txt";
        var catalog = new WindowsSkillCatalog(_workspace, new[] { descriptor });
        Assert.Equal("not_indexed", Assert.Throws<WindowsSkillException>(() => catalog.Read("review", "unknown")).Code);
        Assert.Equal("skills/review/reference.md", Assert.Single(Assert.Single(catalog.Index()).Resources).RelativePath);
    }

    [Fact]
    public void InlineSkillsShareTheIndexedReadSurfaceWithoutAnyWorkspaceOrTemporaryFile()
    {
        var inline = WindowsSkillDescriptor.FromInline("inline", "Inline guidance", "审阅正文🙂", "审阅合同");
        var catalog = WindowsSkillCatalog.FromInline(new[] { inline }); var index = Assert.Single(catalog.Index());
        Assert.True(inline.IsInline); Assert.True(index.IsInline); Assert.Empty(index.RelativePath);
        Assert.Equal("审阅正文🙂", catalog.Read("inline", index.DefinitionDigest).Content);
        Assert.Empty(catalog.Assemble("inline", index.DefinitionDigest).Resources);
        Assert.Equal("workspace_required", Assert.Throws<WindowsSkillException>(() => WindowsSkillCatalog.FromInline(new[] { Descriptor() })).Code);
        var mixed = new WindowsSkillCatalog(_workspace, new[] { Descriptor(), inline });
        Assert.Equal(2, mixed.Index().Count);
        Assert.Equal("size_limit", Assert.Throws<WindowsSkillException>(() => WindowsSkillCatalog.FromInline(new[] { inline },
            new WindowsSkillCatalogOptions { MaximumSkillBytes = 2 }).Index()).Code);
    }

    [Fact]
    public void RevokedTrustRejectsReadsResourcesAssembliesAndNewIndexesImmediately()
    {
        var catalog = Catalog(); var entry = Assert.Single(catalog.Index()); var resource = Assert.Single(entry.Resources);
        catalog.Revoke();
        Assert.Equal("trust_revoked", Assert.Throws<WindowsSkillException>(() => catalog.Read(entry.Name, entry.DefinitionDigest)).Code);
        Assert.Equal("trust_revoked", Assert.Throws<WindowsSkillException>(() => catalog.ReadResource(entry.Name, entry.DefinitionDigest, resource.RelativePath, resource.ContentDigest)).Code);
        Assert.Equal("trust_revoked", Assert.Throws<WindowsSkillException>(() => catalog.Assemble(entry.Name, entry.DefinitionDigest)).Code);
        Assert.Equal("trust_revoked", Assert.Throws<WindowsSkillException>(() => catalog.Index()).Code);
    }

    [Fact]
    public void HostAuthorizationIsCheckedAgainBeforeReturningMaterial()
    {
        var reads = 0; var allowed = true;
        var catalog = WindowsSkillCatalog.FromInline(new[] { WindowsSkillDescriptor.FromInline("inline", "desc", "body") },
            new WindowsSkillCatalogOptions { Authorize = (_, _) => { if (!allowed) throw new WindowsSkillException("host_revoked"); if (++reads == 3) allowed = false; } });
        var entry = Assert.Single(catalog.Index());
        Assert.Equal("host_revoked", Assert.Throws<WindowsSkillException>(() => catalog.Read(entry.Name, entry.DefinitionDigest)).Code);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void CaseInsensitiveAliasesUseTheRegisteredIdentityForTrustRevocation()
    {
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        var authorizedNames = new List<string>();
        var catalog = Catalog(new WindowsSkillCatalogOptions
        {
            Authorize = (name, _) =>
            {
                authorizedNames.Add(name);
                if (revoked.Contains(name)) throw new WindowsSkillException("host_revoked");
            }
        });
        var entry = Assert.Single(catalog.Index()); var resource = Assert.Single(entry.Resources);
        Assert.Contains("审阅指导", catalog.Read("REVIEW", entry.DefinitionDigest).Content);
        revoked.Add(entry.Name);

        Assert.Equal("host_revoked", Assert.Throws<WindowsSkillException>(() => catalog.Read("REVIEW", entry.DefinitionDigest)).Code);
        Assert.Equal("host_revoked", Assert.Throws<WindowsSkillException>(() =>
            catalog.ReadResource("REVIEW", entry.DefinitionDigest, resource.RelativePath, resource.ContentDigest)).Code);
        Assert.Equal("host_revoked", Assert.Throws<WindowsSkillException>(() =>
            catalog.Assemble("REVIEW", entry.DefinitionDigest, new[] { resource.RelativePath })).Code);
        Assert.All(authorizedNames, name => Assert.Equal(entry.Name, name));
    }

    public void Dispose()
    {
        _workspace.Dispose(); Directory.Delete(_directory, true);
    }
}
