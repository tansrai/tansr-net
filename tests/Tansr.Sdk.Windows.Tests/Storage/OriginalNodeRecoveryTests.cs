using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tansr.Sdk.Windows.Storage;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveRecoveryTests;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class OriginalNodeRecoveryTests
{
    [OriginalNodeRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualNodeAndCSharpReadEachOthersPreparedAndCompletedRecoveryLedger(bool migrate)
    {
        using var forward = new Fixture(); JsonElement intent;
        if (migrate) using (var old = await SqliteArchiveStore.OpenAsync(forward.Options())) await old.ReceiveAsync(forward.Input);
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(forward.Options(migrate ? StorageOpenMode.MigrateV1 : StorageOpenMode.Create)))
        {
            if (!migrate) await store.ReceiveAsync(forward.Input);
            intent = await store.PrepareAckRebaseAsync(Request);
        }
        await Node(forward, "confirm", migrate);
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(forward.Options(StorageOpenMode.Reopen)))
        {
            Assert.Null(await store.PendingAsync()); Assert.Null(await store.PendingAckRebaseAsync());
            await store.ConfirmAckRebaseAsync(Result(forward, intent)); Assert.Equal(forward.Body, await store.BodyAsync(forward.Reference));
        }

        using var reverse = new Fixture(); await Node(reverse, "prepare", migrate);
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(reverse.Options(StorageOpenMode.Reopen)))
        {
            var prepared = (await store.PendingAckRebaseAsync())!.Value; Assert.Equal(Text(intent), Text(prepared));
            await store.ConfirmAckRebaseAsync(Result(reverse, prepared));
        }
        await Node(reverse, "read-completed", migrate);
    }

    private static async Task Node(Fixture fixture, string action, bool migrate)
    {
        var options = fixture.Options(); var limits = options.Limits;
        string cliRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TANSR_TEST_RECOVERY_CLI_ROOT")!);
        string payload = JsonSerializer.Serialize(new
        {
            action,
            migrate,
            cliRoot,
            path = fixture.Path,
            identity = fixture.Identity,
            replica = options.Replica,
            limits = new { maxRecords = limits.MaxRecords, maxArtifacts = limits.MaxArtifacts, maxStoredBytes = limits.MaxStoredBytes, maxBatchBytes = limits.MaxBatchBytes },
            maxPages = options.MaxPages,
            scope = fixture.Scope,
            artifact = fixture.Reference,
            bodyBase64 = Convert.ToBase64String(fixture.Body),
            request = Request,
            input = new { binding = fixture.Input.Binding, status = fixture.Input.Status, page = fixture.Input.Page, request = fixture.Input.Request },
        });
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "node", WorkingDirectory = cliRoot, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("--disable-warning=ExperimentalWarning"); process.StartInfo.ArgumentList.Add("--import");
        process.StartInfo.ArgumentList.Add(new Uri(Path.Combine(cliRoot, "node_modules", "tsx", "dist", "loader.mjs")).AbsoluteUri);
        process.StartInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(SourcePath())!, "node-recovery-interop.mjs"));
        Assert.True(process.Start()); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(payload); process.StandardInput.Close(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error); Assert.Equal("original-node-recovery-ok", (await output).Trim());
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}

public sealed class OriginalNodeRecoveryTheoryAttribute : TheoryAttribute
{
    public OriginalNodeRecoveryTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANSR_TEST_RECOVERY_CLI_ROOT")))
            Skip = "需提供已冻结恢复协议 Node 源码目录；本地单元通过不能冒充恢复介质跨实现互通。";
    }
}
