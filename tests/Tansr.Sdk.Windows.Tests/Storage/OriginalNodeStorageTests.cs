using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

/// <summary>原 Node SDK 实际消费，不用 C# 自产夹具代替跨实现验证。未提供原模块时明确显示跳过。</summary>
public sealed class OriginalNodeStorageTests
{
    [OriginalNodeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalNodeAndCSharpReopenTheSameOwnedDatabase(bool encrypted)
    {
        using var fixture = new SqliteArchiveStoreTests.Fixture(encrypted); JsonElement ack;
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options())) ack = await store.ReceiveAsync(fixture.Input);
        await Node(fixture, "reopen-confirm", encrypted);
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)))
        { Assert.Null(await store.PendingAsync()); Assert.Equal(fixture.Body, await store.BodyAsync(fixture.Reference)); await store.ConfirmAsync(fixture.Receipt(ack)); }

        using var reverse = new SqliteArchiveStoreTests.Fixture(encrypted);
        await Node(reverse, "create-pending", encrypted);
        using (var store = await SqliteArchiveStore.OpenAsync(reverse.Options(StorageOpenMode.Reopen)))
        {
            var pending = (await store.PendingAsync())!.Value; Assert.Equal(fixture.Body, await store.BodyAsync(reverse.Reference));
            await store.ConfirmAsync(reverse.Receipt(pending));
        }
        await Node(reverse, "reopen-confirmed", encrypted);
    }

    private static async Task Node(SqliteArchiveStoreTests.Fixture fixture, string action, bool encrypted)
    {
        var options = fixture.Options(); var limits = options.Limits; string cliRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TANSR_TEST_CLI_ROOT")!);
        var payload = JsonSerializer.Serialize(new
        {
            action,
            encrypted,
            cliRoot,
            sourceRevision = WireContract.SourceRevision,
            path = fixture.Path,
            identity = fixture.Identity,
            replica = options.Replica,
            limits = new { maxRecords = limits.MaxRecords, maxArtifacts = limits.MaxArtifacts, maxStoredBytes = limits.MaxStoredBytes, maxBatchBytes = limits.MaxBatchBytes },
            maxPages = options.MaxPages,
            scope = fixture.Scope,
            artifact = fixture.Reference,
            bodyBase64 = Convert.ToBase64String(fixture.Body),
            input = new { binding = fixture.Input.Binding, status = fixture.Input.Status, page = fixture.Input.Page, request = fixture.Input.Request },
        });
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "node", WorkingDirectory = cliRoot, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("--disable-warning=ExperimentalWarning");
        process.StartInfo.ArgumentList.Add("--import"); process.StartInfo.ArgumentList.Add(new Uri(Path.Combine(cliRoot, "node_modules", "tsx", "dist", "loader.mjs")).AbsoluteUri);
        process.StartInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(SourcePath())!, "node-storage-interop.mjs"));
        Assert.True(process.Start()); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(payload); process.StandardInput.Close(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error); Assert.Equal("original-node-consumer-ok", (await output).Trim());
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private static string SourcePath([CallerFilePath] string path = "") => path;
}

public sealed class OriginalNodeTheoryAttribute : TheoryAttribute
{
    public OriginalNodeTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANSR_TEST_CLI_ROOT")))
            Skip = "需提供锁定原 CLI 源码目录，才能执行真实双向数据库消费；普通单元通过不代表已互通。";
    }
}
