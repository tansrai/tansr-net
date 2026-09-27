using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Hosting;

public sealed class DeviceSessionHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationAssemblyFailureNeverPublishesReadyOrStartsPolling(bool foreignScope)
    {
        var client = new Client(); var configured = false;
        using var host = new DeviceSessionHost(client, new Backend(), new Journal(), new DeviceSessionOptions
        {
            SessionId = "session-1",
            WorkspaceId = "workspace-1",
            AfterBindingAsync = (_, _) => { configured = true; return Task.CompletedTask; },
            ExecutionNotifications = (_, _) =>
            {
                Assert.True(configured); Assert.DoesNotContain("poll", client.Actions);
                if (!foreignScope) throw new IOException("synthetic notification assembly failure");
                var node = System.Text.Json.Nodes.JsonNode.Parse(client.ReadScope().GetRawText())!;
                node["endUserId"] = "another-user";
                return Task.FromResult<IExecutionNotificationSource>(new Notifications(JsonSerializer.SerializeToElement(node)));
            }
        }, (_, _) => Task.CompletedTask);
        if (foreignScope) await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        else await Assert.ThrowsAsync<IOException>(() => host.StartAsync());
        Assert.Equal(DeviceSessionState.Failed, host.State); Assert.DoesNotContain("poll", client.Actions);
    }

    [Fact]
    public async Task NotificationsAreAssembledOnceAfterBindingAndBeforeDeviceIsReady()
    {
        var client = new Client(); var calls = 0;
        using var host = new DeviceSessionHost(client, new Backend(), new Journal(), new DeviceSessionOptions
        {
            SessionId = "session-1",
            WorkspaceId = "workspace-1",
            ExecutionNotifications = (connection, _) =>
            {
                calls++; Assert.Contains("bind", client.Actions); Assert.DoesNotContain("poll", client.Actions);
                Assert.Equal("executor-1", connection.GetProperty("executorId").GetString());
                return Task.FromResult<IExecutionNotificationSource>(new Notifications(client.ReadScope()));
            }
        }, (_, _) => Task.CompletedTask);
        await host.StartAsync(); Assert.Equal(1, calls); Assert.Equal(DeviceSessionState.Ready, host.State);
        await host.StopAsync(); Assert.Equal(DeviceSessionState.Stopped, host.State);
    }

    private sealed class Notifications(JsonElement scope) : IExecutionNotificationSource
    {
        public JsonElement Scope => scope;
        public Task ObserveAsync(JsonElement connection, long? lastEventId, Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
        public Task<JsonElement> GetStatusAsync(JsonElement operation, CancellationToken cancellationToken) => throw new InvalidOperationException("No operation was started.");
    }

    [Fact]
    public async Task InitializesBindsThenPollsAndAwaitsDeviceShutdown()
    {
        var client = new Client();
        var journal = new Journal();
        using var host = New(client, journal);
        await host.StartAsync();
        Assert.Equal(DeviceSessionState.Ready, host.State);
        Assert.Equal(new[] { "initialize", "register", "initialize", "bind", "poll" }, client.Actions);
        Assert.Equal("workspace-1", client.Target!.Value.GetProperty("workspaceId").GetString());
        Assert.Equal("1", client.Target.Value.GetProperty("workspaceRevision").GetString());
        Assert.Equal(new string('a', 64), client.BindingRequest!.Value.GetProperty("expectedCapabilityRevision").GetString());
        Assert.False(host.Completion.IsCompleted);
        await host.StopAsync();
        Assert.True(client.PollStopped);
        Assert.Equal(DeviceSessionState.Stopped, host.State);
        Assert.False(journal.Closed); // externally owned storage remains usable
    }

    [Fact]
    public async Task ControllerBindingAndExecutorPollingKeepSeparateClients()
    {
        var controller = new Client(); var executor = new Client(); var configured = false;
        using var host = new DeviceSessionHost(controller, executor, new Backend(), new Journal(),
            new DeviceSessionOptions
            {
                SessionId = "session-1",
                WorkspaceId = "workspace-1",
                AfterBindingAsync = (bound, _) =>
            {
                Assert.Equal(JsonValueKind.Object, bound.GetProperty("binding").ValueKind);
                Assert.DoesNotContain("poll", executor.Actions);
                configured = true; return Task.CompletedTask;
            }
            }, (_, _) => Task.CompletedTask);
        await host.StartAsync();
        Assert.True(configured);
        Assert.Equal(new[] { "initialize", "initialize", "bind" }, controller.Actions);
        Assert.Equal(new[] { "register", "poll" }, executor.Actions);
        await host.StopAsync();
    }

    [Fact]
    public async Task TerminalBindingFailurePreventsReadyAndPolling()
    {
        var client = new Client();
        using var host = new DeviceSessionHost(client, new Backend(), new Journal(),
            new DeviceSessionOptions
            {
                SessionId = "session-1",
                WorkspaceId = "workspace-1",
                AfterBindingAsync = (_, _) => throw new IOException("synthetic terminal binding failed")
            }, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => host.StartAsync());
        Assert.Equal(DeviceSessionState.Failed, host.State);
        Assert.DoesNotContain("poll", client.Actions);
    }

    [Fact]
    public async Task IdentityChangesDuringInitializationPreventRegistration()
    {
        var client = new Client { ChangeIdentity = true };
        using var host = New(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        Assert.Equal(new[] { "initialize" }, client.Actions);
        Assert.Equal(DeviceSessionState.Failed, host.State);
    }

    [Fact]
    public async Task ReplacementConnectionBindsTheRefreshedCapabilityRevisionUsingTheSameInitialization()
    {
        var client = new Client { ChangeCapabilityAtRegister = true };
        using var host = New(client);
        await host.StartAsync();
        Assert.Equal(new[] { "initialize", "register", "initialize", "bind", "poll" }, client.Actions);
        Assert.Equal(new string('b', 64), client.BindingRequest!.Value.GetProperty("expectedCapabilityRevision").GetString());
        Assert.Equal(2, client.Initializations.Count);
        Assert.Equal(client.Initializations[0].GetRawText(), client.Initializations[1].GetRawText());
        Assert.Equal("session-1", client.BindingRequest.Value.GetProperty("sessionId").GetString());
        Assert.Equal(DeviceSessionState.Ready, host.State);
        await host.StopAsync(); Assert.True(client.PollStopped);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("session")]
    [InlineData("platform")]
    public async Task ForeignAuthorityDuringCapabilityRefreshPreventsBindingAndPolling(string changed)
    {
        var client = new Client { ChangeOnRefresh = changed };
        using var host = New(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        Assert.Equal(new[] { "initialize", "register", "initialize" }, client.Actions);
        Assert.Null(client.BindingRequest); Assert.Equal(DeviceSessionState.Failed, host.State);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.Completion);
    }

    [Fact]
    public async Task LostCapabilityRefreshNeverRetriesInitializationOrBinds()
    {
        var client = new Client { LoseRefreshResponse = true };
        using var host = New(client);
        await Assert.ThrowsAsync<IOException>(() => host.StartAsync());
        Assert.Equal(new[] { "initialize", "register", "initialize" }, client.Actions);
        Assert.Null(client.BindingRequest); Assert.Equal(DeviceSessionState.Failed, host.State);
        await Assert.ThrowsAsync<IOException>(() => host.Completion);
    }

    [Fact]
    public async Task BindingResponseLossDoesNotRetryOrPoll()
    {
        var client = new Client { LoseBindingResponse = true };
        using var host = New(client);
        await Assert.ThrowsAsync<IOException>(() => host.StartAsync());
        Assert.Equal(new[] { "initialize", "register", "initialize", "bind" }, client.Actions);
        Assert.Equal(DeviceSessionState.Failed, host.State);
        await Assert.ThrowsAsync<IOException>(() => host.Completion);
    }

    [Fact]
    public async Task ForgedConnectionRevisionIsRejectedBeforePolling()
    {
        var client = new Client { SubstituteTarget = true };
        using var host = New(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        Assert.DoesNotContain("poll", client.Actions);
    }

    [Fact]
    public async Task StartupTimeoutCancelsInitializationAndNeverLaunchesDeviceWork()
    {
        var client = new Client { StallInitialization = true };
        using var host = New(client, timeout: TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync());
        await host.Completion;
        Assert.Equal(DeviceSessionState.Stopped, host.State);
        Assert.Equal(new[] { "initialize" }, client.Actions);
    }

    [Fact]
    public async Task DisposingOnlyRequestsStopAndTheLifecycleRemainsAwaitable()
    {
        var client = new Client();
        var host = New(client);
        await host.StartAsync();
        host.Dispose();
        await host.StopAsync();
        Assert.True(client.PollStopped);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync());
    }

    [Fact]
    public async Task AnotherRegisteredWorkspaceCannotReplaceTheConfirmedSessionBinding()
    {
        var client = new Client { PollOperation = ExecutionFixture.Operation(workspace: "workspace-2") };
        var backend = new Backend { ExtraWorkspace = true };
        var authorized = 0;
        using var host = new DeviceSessionHost(client, backend, new Journal(),
            new DeviceSessionOptions { SessionId = "session-1", WorkspaceId = "workspace-1" },
            (_, _) => { authorized++; return Task.CompletedTask; });
        await host.StartAsync();
        await Assert.ThrowsAsync<ExecutionRejectedException>(() => host.Completion);
        Assert.Equal(0, authorized);
        Assert.Equal(DeviceSessionState.Failed, host.State);
    }

    [Fact]
    public async Task ANewLoginCannotTakeOverTheOldDeviceHostEvenWithMatchingCurrentOperationScope()
    {
        var client = new Client { PollOperation = ExecutionFixture.Operation(user: "other"), ChangeIdentityAtPoll = true };
        using var host = New(client);
        await host.StartAsync();
        await Assert.ThrowsAsync<ExecutionRejectedException>(() => host.Completion);
        Assert.Equal(DeviceSessionState.Failed, host.State);
    }

    [Fact]
    public void UnknownWorkspaceAndDuplicateToolsFailWithoutIo()
    {
        var client = new Client();
        Assert.Throws<ArgumentException>(() => new DeviceSessionHost(client, new Backend(), new Journal(),
            new DeviceSessionOptions { SessionId = "session-1", WorkspaceId = "other" }, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentException>(() => new DeviceSessionHost(client, new Backend(), new Journal(),
            new DeviceSessionOptions { SessionId = "session-1", WorkspaceId = "workspace-1", RequestedTools = ["Read", "Read"] }, (_, _) => Task.CompletedTask));
        Assert.Empty(client.Actions);
    }

    private static DeviceSessionHost New(Client client, Journal? journal = null, TimeSpan? timeout = null) =>
        new(client, new Backend(), journal ?? new Journal(), new DeviceSessionOptions
        { SessionId = "session-1", WorkspaceId = "workspace-1", RequestedTools = ["Read"], ConnectionTimeout = timeout ?? TimeSpan.FromSeconds(5) },
            (_, _) => Task.CompletedTask);

    private sealed class Backend : IExecutionBackend
    {
        public bool ExtraWorkspace;
        public JsonElement Registration => ExtraWorkspace ? ExecutionFixture.Set(ExecutionFixture.Registration(), "workspaces",
            JsonSerializer.SerializeToElement(new[] { new { workspaceId = "workspace-1", revision = "1" }, new { workspaceId = "workspace-2", revision = "1" } })) : ExecutionFixture.Registration();
        public Task<JsonElement> ExecuteAsync(JsonElement op, Func<CancellationToken, Task> guard, CancellationToken ct) =>
            throw new InvalidOperationException("This fixture must never execute an operation.");
    }

    private sealed class Client : IDeviceExecutionClient
    {
        public readonly List<string> Actions = [];
        public readonly List<JsonElement> Initializations = [];
        public bool ChangeIdentity, LoseBindingResponse, SubstituteTarget, StallInitialization, PollStopped;
        public bool ChangeIdentityAtPoll, ChangeCapabilityAtRegister, LoseRefreshResponse;
        public string? ChangeOnRefresh;
        public JsonElement? PollOperation;
        public JsonElement? Target, BindingRequest;
        private string user = "user";
        private string capabilityRevision = new('a', 64);
        public JsonElement ReadScope() => ExecutionFixture.Scope(user);
        public async Task<JsonElement> InitializeAsync(JsonElement input, CancellationToken ct)
        {
            Actions.Add("initialize");
            WireJson.ValidateNamed("SessionInitializeRequest", input);
            Initializations.Add(input.Clone());
            if (StallInitialization) await Task.Delay(Timeout.Infinite, ct);
            if (ChangeIdentity) user = "other";
            var refresh = Initializations.Count > 1;
            if (refresh && LoseRefreshResponse) throw new IOException("synthetic refresh response loss");
            if (refresh && ChangeOnRefresh == "scope") user = "other";
            var capabilities = Capabilities(null);
            if (refresh && ChangeOnRefresh == "session") return ExecutionFixture.Set(capabilities, "sessionId", JsonSerializer.SerializeToElement("other-session"));
            if (refresh && ChangeOnRefresh == "platform")
                return ExecutionFixture.Set(capabilities, "platform", ExecutionFixture.Set(capabilities.GetProperty("platform"), "platform", JsonSerializer.SerializeToElement("linux")));
            return capabilities;
        }
        public Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken ct)
        {
            Actions.Add("register");
            if (ChangeCapabilityAtRegister) capabilityRevision = new string('b', 64);
            return Task.FromResult(ExecutionFixture.Connection());
        }
        public Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken ct) => Task.FromResult(connection);
        public Task<JsonElement> BindExecutionAsync(JsonElement request, JsonElement target, CancellationToken ct)
        {
            Actions.Add("bind"); WireJson.ValidateNamed("ExecutionBindingRequest", request);
            WireJson.ValidateNamed("ExecutionTarget", target);
            Target = target; BindingRequest = request;
            if (request.GetProperty("expectedCapabilityRevision").GetString() != capabilityRevision) throw new InvalidDataException("stale_generation");
            if (LoseBindingResponse) throw new IOException("synthetic response loss");
            if (SubstituteTarget) target = ExecutionFixture.Set(target, "connectionRevision", JsonSerializer.SerializeToElement("2"));
            return Task.FromResult(Capabilities(JsonSerializer.SerializeToElement(new { bindingId = "binding-1", revision = "1", target })));
        }
        public async Task<JsonElement> PollAsync(JsonElement connection, CancellationToken ct)
        {
            Actions.Add("poll");
            if (ChangeIdentityAtPoll) user = "other";
            if (PollOperation.HasValue) return ExecutionFixture.Batch(PollOperation.Value);
            try { await Task.Delay(Timeout.Infinite, ct); } finally { PollStopped = true; }
            return ExecutionFixture.Batch();
        }
        public Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> GetStatusAsync(string session, string operation, CancellationToken ct) => throw new NotSupportedException();
        private JsonElement Capabilities(JsonElement? binding) => JsonSerializer.SerializeToElement(new
        {
            protocol = "sdk2-ext-v1",
            sessionId = "session-1",
            platform = ExecutionFixture.Registration().GetProperty("platform"),
            capabilityRevision,
            effectiveTools = Array.Empty<object>(),
            binding
        });
    }

    private sealed class Journal : IExecutorJournal
    {
        public bool Closed;
        public Task<ExecutorJournalClaim> ClaimAsync(JsonElement operation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAsync(JsonElement operation, JsonElement receipt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement?> ReceiptAsync(JsonElement operation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
    }
}
