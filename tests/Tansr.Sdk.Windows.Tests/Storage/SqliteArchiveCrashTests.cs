using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using Fixture = Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests.Fixture;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveCrashTests
{
    private const string ChildVariable = "TANSR_NET_ARCHIVE_CRASH_CHILD";
    private const string PhaseVariable = "TANSR_NET_ARCHIVE_CRASH_PHASE";

    [Fact]
    public async Task KilledWriterPreservesOnlyCommittedOriginalArchiveAndAck()
    {
        string? childDirectory = Environment.GetEnvironmentVariable(ChildVariable);
        if (childDirectory != null)
        {
            using var child = new Fixture(directory: childDirectory); using var store = await SqliteArchiveStore.OpenAsync(child.Options());
            var connection = (SqliteConnection)typeof(SqliteArchiveStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            void Block()
            {
                File.WriteAllText(Path.Combine(childDirectory, "writer-ready"), Environment.GetEnvironmentVariable(PhaseVariable));
                Thread.Sleep(Timeout.Infinite);
            }
            WalHook? hook = null;
            if (Environment.GetEnvironmentVariable(PhaseVariable) == "committed")
            { hook = (_, _, _, _) => { Block(); return 0; }; SqliteWalHook(connection.Handle!.DangerousGetHandle(), hook, IntPtr.Zero); }
            else SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, _ => { Block(); return 0; }, null);
            await store.ReceiveAsync(child.Input); GC.KeepAlive(hook);
            throw new InvalidOperationException("Writer must be killed at the controlled SQLite boundary.");
        }

        foreach (string phase in new[] { "uncommitted", "committed" })
        {
            using var f = new Fixture();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(SqliteArchiveCrashTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + typeof(SqliteArchiveCrashTests).FullName + "." + nameof(KilledWriterPreservesOnlyCommittedOriginalArchiveAndAck));
            start.Environment[ChildVariable] = f.Directory; start.Environment[PhaseVariable] = phase;
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                string ready = Path.Combine(f.Directory, "writer-ready"); var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!File.Exists(ready) && !process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(30);
                if (!File.Exists(ready)) throw new InvalidOperationException("Controlled writer did not reach " + phase + (process.HasExited ? ": " + await stdout + await stderr : "."));
                Assert.Equal(phase, File.ReadAllText(ready));
                // Actual second opener while an EXCLUSIVE SQLite writer owns the file; no fake storage error.
                await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(f.Options(StorageOpenMode.Reopen)));
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
                using var reopened = await SqliteArchiveStore.OpenAsync(f.Options(StorageOpenMode.Reopen));
                if (phase == "uncommitted") { Assert.Null(await reopened.HeadAsync()); Assert.Null(await reopened.PendingAsync()); }
                else
                {
                    var original = (await reopened.PendingAsync())!.Value; Assert.Equal("ack", original.GetProperty("request").GetProperty("requestId").GetString());
                    Assert.Equal(f.Body, await reopened.BodyAsync(f.Reference)); await reopened.ConfirmAsync(f.Receipt(original));
                    Assert.Null(await reopened.PendingAsync()); Assert.Equal("1", (await reopened.HeadAsync())!.Value.GetProperty("sequence").GetString());
                }
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await stdout; await stderr; }
        }
    }

    [Fact]
    public async Task PhysicalSqliteFullRollsBackWithoutInventingDurableAck()
    {
        using var f = new Fixture(body: new byte[262144]); var options = f.Options(); options.MaxPages = 32;
        using (var store = await SqliteArchiveStore.OpenAsync(options))
        {
            Assert.Equal("storage_error", (await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(f.Input))).Code);
            Assert.Null(await store.PendingAsync()); Assert.Null(await store.HeadAsync());
        }
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteArchiveStore.OpenAsync(options); Assert.Null(await reopened.HeadAsync()); Assert.Null(await reopened.PendingAsync());
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WalHook(IntPtr argument, IntPtr database, IntPtr name, int pages);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_wal_hook", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SqliteWalHook(IntPtr database, WalHook? callback, IntPtr argument);
}
