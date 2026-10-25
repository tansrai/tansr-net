using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Tansr.Sdk.Transport;
using WpfAssistant;

namespace Tansr.Sdk.Tests.Examples;

[Collection("ConnectionTiming")]
public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public async Task SourceCreatedAfterExpiryCannotReenableTheFinishedListener()
    {
        using var files = new DiagnosticFiles();
        using var diagnostic = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMilliseconds(1)));
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // SDK 静态源可能已被其它用例初始化；重投同一创建通知，独立验证到期后的门。
        var lateSource = TansrConnectionEventSource.Log;
        typeof(ConnectionDiagnostics).GetMethod("OnEventSourceCreated", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(diagnostic, new object[] { lateSource });
        Assert.False(lateSource.IsEnabled());
        Assert.False(File.Exists(files.Output));
    }

    [Fact]
    public void NormalizedWindowsNamespaceIsRejectedBeforeFileChecks()
    {
        using var files = new DiagnosticFiles();
        // 使用本机扩展路径复现斜杠规范化，既不访问 SMB 也不依赖网络共享存在。
        var localNamespace = "//?/" + files.Output.Replace('\\', '/');
        Assert.StartsWith(@"\\", Path.GetFullPath(localNamespace));
        using var diagnostic = ConnectionDiagnostics.Create(localNamespace, TimeSpan.FromMinutes(1));
        Assert.Null(diagnostic);
        Assert.False(File.Exists(files.Output));
    }

    [Fact]
    public void InvalidPathAndTtlDoNotEnableDiagnostics()
    {
        using var files = new DiagnosticFiles();
        Assert.Null(ConnectionDiagnostics.Create(null, TimeSpan.FromMinutes(1)));
        Assert.Null(ConnectionDiagnostics.Create("relative.jsonl", TimeSpan.FromMinutes(1)));
        Assert.Null(ConnectionDiagnostics.Create(@"\\server\share\trace.jsonl", TimeSpan.FromMinutes(1)));
        Assert.Null(ConnectionDiagnostics.Create(files.Output, TimeSpan.Zero));
        Assert.Null(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(31)));
        Assert.Null(ConnectionTiming.Start(true));
        Assert.False(File.Exists(files.Output));
    }

    [Fact]
    public async Task WritesOnlyFirstSampleAfterCompletionWithoutBusinessData()
    {
        using var files = new DiagnosticFiles();
        using var diagnostic = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1)));
        var timing = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(true));
        timing.Mark(ConnectionTimingPhase.RequestSerialized);
        var foreign = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(false));
        foreign.Mark(ConnectionTimingPhase.Failed);
        TansrConnectionEventSource.Log.Stage(Guid.NewGuid().ToString("N"), "SECRET-BODY bearer C:\\private\\file.txt", Stopwatch.GetTimestamp());
        timing.Mark(ConnectionTimingPhase.CreateHeaders);
        Assert.False(File.Exists(files.Output));
        timing.Mark(ConnectionTimingPhase.Complete);
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(ConnectionTiming.Start(false));
        var rows = Read(files.Output);
        Assert.Equal(new[] { "ResumeBegin", "RequestSerialized", "CreateHeaders", "Complete" }, rows.Select(Phase));
        Assert.Single(rows.Select(row => row.GetProperty("sample").GetString()).Distinct());
        var expectedFields = new[] { "elapsedMs", "phase", "sample", "timestamp", "utc" };
        Assert.All(rows, row => Assert.Equal(expectedFields, row.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)));
        Assert.Equal(0d, rows[0].GetProperty("elapsedMs").GetDouble());
        Assert.True(rows[^1].GetProperty("elapsedMs").GetDouble() >= 0d);
        var text = File.ReadAllText(files.Output);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("bearer", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain(files.Output, text);
        Assert.True(new FileInfo(files.Output).Length <= 16384);
    }

    [Fact]
    public async Task ExpiryStopsAnIncompleteSampleWithoutWaitingForTheRequest()
    {
        using var files = new DiagnosticFiles();
        var now = Stopwatch.GetTimestamp();
        using var diagnostic = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1), () => now));
        var timing = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(true));
        timing.Mark(ConnectionTimingPhase.CapabilityRequestReady);
        now += 61 * Stopwatch.Frequency;
        timing.Mark(ConnectionTimingPhase.CapabilityHeaders);
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        timing.Mark(ConnectionTimingPhase.Complete);

        Assert.Equal(new[] { "ResumeBegin", "CapabilityRequestReady", "Expired" }, Read(files.Output).Select(Phase));
        Assert.Null(ConnectionTiming.Start(true));
    }

    [Fact]
    public async Task RecordAndByteCapsEndTheSample()
    {
        using var files = new DiagnosticFiles();
        using var diagnostic = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1)));
        var timing = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(true));
        for (var i = 0; i < 100; i++) timing.Mark(ConnectionTimingPhase.DiscoveryGateWait);
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(Read(files.Output).Length, 1, 64);
        Assert.InRange(new FileInfo(files.Output).Length, 1, 16384);
        Assert.Null(ConnectionTiming.Start(true));
    }

    [Fact]
    public async Task FileFailureAndEvidenceCollisionDoNotEscapeOrOverwrite()
    {
        using var files = new DiagnosticFiles();
        using (var diagnostic = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1))))
        {
            var timing = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(true));
            File.WriteAllText(files.Output, "existing evidence");
            timing.Mark(ConnectionTimingPhase.Complete);
            await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("existing evidence", File.ReadAllText(files.Output));
        }
        Assert.Null(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1)));
        var second = Path.Combine(files.Directory, "directory.jsonl");
        using var failure = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(second, TimeSpan.FromMinutes(1)));
        var next = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(true));
        Directory.CreateDirectory(second);
        next.Mark(ConnectionTimingPhase.Complete);
        await failure.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Directory.Exists(second));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task ClosingReleasesTheListenerAndPreservesTheIncompleteSample()
    {
        using var files = new DiagnosticFiles();
        var diagnostic = Assert.IsType<ConnectionDiagnostics>(ConnectionDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1)));
        var timing = Assert.IsType<ConnectionTiming>(ConnectionTiming.Start(true));
        timing.Mark(ConnectionTimingPhase.CreateRequestReady);
        diagnostic.Dispose();
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        timing.Mark(ConnectionTimingPhase.Complete);
        Assert.Equal(new[] { "ResumeBegin", "CreateRequestReady", "Closed" }, Read(files.Output).Select(Phase));
        Assert.Null(ConnectionTiming.Start(true));
    }

    private static JsonElement[] Read(string path) => File.ReadAllLines(path).Select(line =>
    {
        using var value = JsonDocument.Parse(line);
        return value.RootElement.Clone();
    }).ToArray();
    private static string Phase(JsonElement row) => row.GetProperty("phase").GetString()!;
    private sealed class DiagnosticFiles : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "tansr-connection-timing-" + Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Directory, "connection.jsonl");
        internal DiagnosticFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
