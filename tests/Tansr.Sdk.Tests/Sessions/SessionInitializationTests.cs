using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Sessions;

public sealed class SessionInitializationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task PersistenceReadyPrecedesAllHttpAndOwnedLeaseTransfersToConnection()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        var lease = new PersistenceLease();
        CancellationToken persistenceToken = default;
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            PreparePersistenceAsync = async token =>
            {
                persistenceToken = token; entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return lease;
            }
        }, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal(SessionInitializationStage.PreparingPersistence, initialization.Progress.Stage);
        Assert.False(initialization.Accepted.IsCompleted);
        Assert.False(initialization.Ready.IsCompleted);
        Assert.Empty(fixture.Handler.Requests);
        release.TrySetResult();
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        Assert.Equal(new[] { "GET /api/capabilities/sessions", "POST /api/sessions", "POST /api/sessions/session/initialize" }, fixture.Trace.ToArray());
        cancellation.Cancel(); initialization.Cancel(); initialization.Dispose();
        Assert.False(persistenceToken.IsCancellationRequested);
        Assert.Equal(0, lease.Stops);
        Assert.False(connection.Completion.IsCompleted);
        await connection.StopAsync().WaitAsync(Deadline);
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.True(persistenceToken.IsCancellationRequested);
        Assert.Equal(1, lease.Stops);
        AssertNoBusinessRequests(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceFailureOrCooperativeCancellationPreventsAllHttp(bool cancel)
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal();
        var failure = new InvalidOperationException("synthetic persistence readiness");
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            PreparePersistenceAsync = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw failure;
            }
        });
        await entered.Task.WaitAsync(Deadline);
        if (cancel) initialization.Cancel(); else release.TrySetResult();
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Accepted.WaitAsync(Deadline));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
        }
        else
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => initialization.Accepted.WaitAsync(Deadline)));
            Assert.Same(failure, await Record.ExceptionAsync(() => initialization.Ready.WaitAsync(Deadline)));
        }
        Assert.Null(initialization.CleanupFailure);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task PersistenceIgnoredCancellationStopsLateOwnedLeaseOnceWithoutCreatingSession()
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal();
        var lease = new PersistenceLease();
        CancellationToken persistenceToken = default;
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(30),
            PreparePersistenceAsync = async token =>
            {
                persistenceToken = token; entered.TrySetResult();
                await release.Task;
                return lease;
            }
        });
        await entered.Task.WaitAsync(Deadline);
        try
        {
            initialization.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Accepted.WaitAsync(Deadline));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
            Assert.Equal("session_cleanup_unconfirmed", Assert.IsType<TimeoutException>(initialization.CleanupFailure).Message);
            Assert.Equal(0, lease.Stops);
            Assert.True(persistenceToken.WaitHandle.WaitOne(0));
            Assert.Empty(fixture.Handler.Requests);
            release.TrySetResult();
            await lease.Stopped.Task.WaitAsync(Deadline);
            Assert.Equal(1, lease.Stops);
            Assert.Empty(fixture.Handler.Requests);
            Assert.True(initialization.Accepted.IsCanceled);
            Assert.True(initialization.Ready.IsCanceled);
        }
        finally
        {
            initialization.Cancel(); release.TrySetResult();
            await lease.Stopped.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task PersistenceResidentBorrowWithoutLeaseSupportsReadyAndLocalStop()
    {
        using var fixture = new Fixture();
        var borrowed = 0;
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            PreparePersistenceAsync = _ =>
            { Interlocked.Increment(ref borrowed); return Task.FromResult<ISessionPersistenceLease?>(null); }
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        Assert.Equal(1, borrowed);
        var requests = fixture.Handler.Requests.Count;
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.True(connection.Completion.IsCompletedSuccessfully);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        AssertNoBusinessRequests(fixture);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("principal")]
    public async Task PersistenceIdentityDriftStopsOwnedLeaseAndPreventsAllHttp(string identity)
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal();
        var lease = new PersistenceLease();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            PreparePersistenceAsync = async token =>
            { entered.TrySetResult(); await release.Task.WaitAsync(token); return lease; }
        });
        await entered.Task.WaitAsync(Deadline);
        if (identity == "scope") fixture.AuthorizationRevision = "3";
        else fixture.Principal = "app/another";
        release.TrySetResult();
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => initialization.Ready.WaitAsync(Deadline));
        Assert.Equal("context_changed", error.Code);
        Assert.Same(error, await Record.ExceptionAsync(() => initialization.Accepted.WaitAsync(Deadline)));
        Assert.Equal(1, lease.Stops);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("faulted")]
    [InlineData("pending")]
    public async Task PersistenceCleanupPreservesAcceptedSessionAndOriginalLaterFailure(string cleanup)
    {
        using var fixture = new Fixture();
        var failure = new InvalidOperationException("synthetic initialization after persistence");
        var cleanupFailure = new IOException("synthetic persistence cleanup");
        var stopRelease = Signal(); var stopFinished = Signal();
        var lease = new PersistenceLease(async () =>
        {
            try
            {
                if (cleanup == "pending") await stopRelease.Task;
                if (cleanup == "faulted") throw cleanupFailure;
            }
            finally { stopFinished.TrySetResult(); }
        });
        var services = new Services();
        fixture.Handler.Respond = (request, _) => request.Path.EndsWith("/initialize", StringComparison.Ordinal)
            ? Task.FromException<HttpResponseMessage>(failure) : Task.FromResult(fixture.Handler.Default(request));
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(30),
            PreparePersistenceAsync = _ => Task.FromResult<ISessionPersistenceLease?>(lease),
            RequiredServices = services
        });
        try
        {
            var accepted = await initialization.Accepted.WaitAsync(Deadline);
            Assert.Same(failure, await Record.ExceptionAsync(() => initialization.Ready.WaitAsync(Deadline)));
            Assert.Same(accepted, await initialization.Accepted);
            Assert.Equal("session", accepted.Id);
            Assert.Equal(1, services.Stops);
            Assert.Equal(1, lease.Stops);
            if (cleanup == "pending")
            {
                Assert.False(stopFinished.Task.IsCompleted);
                Assert.Equal("session_cleanup_unconfirmed", Assert.IsType<TimeoutException>(initialization.CleanupFailure).Message);
            }
            else if (cleanup == "faulted") Assert.Same(cleanupFailure, initialization.CleanupFailure);
            else Assert.Null(initialization.CleanupFailure);
            Assert.Single(fixture.Handler.Requests, request => request.Path == "/api/sessions");
            AssertNoBusinessRequests(fixture);
        }
        finally
        {
            stopRelease.TrySetResult();
            await stopFinished.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task PersistenceThrowingCancellationNotificationCannotReplacePreparationFailureOrSkipOwnedStop()
    {
        using var fixture = new Fixture();
        var notificationFailure = new InvalidOperationException("synthetic cancellation notification");
        var preparationFailure = new IOException("synthetic later preparation");
        var lease = new PersistenceLease();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            PreparePersistenceAsync = token =>
            {
                _ = token.Register(() => throw notificationFailure);
                return Task.FromResult<ISessionPersistenceLease?>(lease);
            },
            PrepareObservationAsync = (_, _) => Task.FromException<SessionObservationOptions?>(preparationFailure)
        });
        var accepted = await initialization.Accepted.WaitAsync(Deadline);
        Assert.Same(preparationFailure, await Record.ExceptionAsync(() => initialization.Ready.WaitAsync(Deadline)));
        Assert.Same(accepted, await initialization.Accepted);
        Assert.Equal(1, lease.Stops);
        Assert.True(ContainsException(initialization.CleanupFailure, notificationFailure));
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task PersistenceThrowingCancellationNotificationRemainsObservableAndOwnedStopStillRuns()
    {
        using var fixture = new Fixture();
        var notificationFailure = new InvalidOperationException("synthetic cancellation notification");
        var lease = new PersistenceLease();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            PreparePersistenceAsync = token =>
            {
                _ = token.Register(() => throw notificationFailure);
                return Task.FromResult<ISessionPersistenceLease?>(lease);
            }
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        var requests = fixture.Handler.Requests.Count;
        var stopFailure = await Record.ExceptionAsync(() => connection.StopAsync().WaitAsync(Deadline));
        await lease.Stopped.Task.WaitAsync(Deadline);
        var completionFailure = await Record.ExceptionAsync(() => connection.Completion.WaitAsync(Deadline));
        Assert.True(ContainsException(stopFailure, notificationFailure) || ContainsException(completionFailure, notificationFailure));
        Assert.Equal(1, lease.Stops);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task PersistenceSynchronousProviderPrefixCannotBlockReturningLoadingAndLateLeaseIsStopped()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = Signal(); var lease = new PersistenceLease();
        var invocation = Task.Run(() => fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(30),
            PreparePersistenceAsync = _ =>
            {
                entered.TrySetResult();
                release.Wait();
                return Task.FromResult<ISessionPersistenceLease?>(lease);
            }
        }, cancellation.Token));
        await entered.Task.WaitAsync(Deadline);
        try
        {
            // 调用线程必须先拿到 loading 句柄，不能等同步 provider 前半段结束。
            var initialization = await invocation.WaitAsync(Deadline);
            Assert.False(initialization.Accepted.IsCompleted);
            Assert.False(initialization.Ready.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
            Assert.Equal("session_cleanup_unconfirmed", Assert.IsType<TimeoutException>(initialization.CleanupFailure).Message);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(0, lease.Stops);
            release.Set();
            await lease.Stopped.Task.WaitAsync(Deadline);
            Assert.Equal(1, lease.Stops);
            Assert.Empty(fixture.Handler.Requests);
        }
        finally
        {
            cancellation.Cancel(); release.Set();
            var initialization = await invocation.WaitAsync(Deadline);
            initialization.Dispose();
            await lease.Stopped.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task StagesWaitForTheirPredecessorAndReadyRequiresActualEventHeaders()
    {
        using var fixture = new Fixture();
        var confirmed = Signal(); var confirmRelease = Signal();
        var serviceEntered = Signal(); var serviceRelease = Signal();
        var preparation = Signal(); var prepareRelease = Signal();
        var headers = Signal(); var headerRelease = Signal();
        var services = new Services(async (_, token) =>
        {
            fixture.Trace.Enqueue("services"); serviceEntered.SetResult();
            await serviceRelease.Task.WaitAsync(token);
        });
        fixture.Handler.Respond = async (request, token) =>
        {
            if (request.Path.EndsWith("/events", StringComparison.Ordinal))
            { headers.SetResult(); await headerRelease.Task.WaitAsync(token); }
            return fixture.Handler.Default(request);
        };
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            SessionConfirmedAsync = async (_, token) =>
            {
                fixture.Trace.Enqueue("confirmed"); confirmed.SetResult();
                await confirmRelease.Task.WaitAsync(token);
            },
            RequiredServices = services,
            PrepareObservationAsync = async (_, token) =>
            {
                fixture.Trace.Enqueue("prepare"); preparation.SetResult();
                await prepareRelease.Task.WaitAsync(token);
                return Observation();
            }
        });
        await confirmed.Task.WaitAsync(Deadline);
        var session = await initialization.Accepted.WaitAsync(Deadline);
        Assert.Equal("session", session.Id); Assert.False(serviceEntered.Task.IsCompleted);
        Assert.False(initialization.Ready.IsCompleted);
        confirmRelease.SetResult(); await serviceEntered.Task.WaitAsync(Deadline);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/initialize", StringComparison.Ordinal));
        serviceRelease.SetResult(); await preparation.Task.WaitAsync(Deadline);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal));
        prepareRelease.SetResult(); await headers.Task.WaitAsync(Deadline);
        Assert.False(initialization.Ready.IsCompleted);
        headerRelease.SetResult();
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        Assert.Same(session, connection.Session);
        Assert.NotNull(connection.Capabilities);
        Assert.Equal(new[]
        {
            "GET /api/capabilities/sessions", "POST /api/sessions", "confirmed", "services",
            "POST /api/sessions/session/initialize", "prepare", "GET /api/sessions/session/events"
        }, fixture.Trace.ToArray());
        Assert.False(connection.Completion.IsCompleted);
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.Equal(1, services.Stops);
        Assert.True(services.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task Sdk1DoesNotInitializeCapabilitiesOrReadBusinessHistory()
    {
        using var fixture = new Fixture(SessionContract.Sdk1);
        using var initialization = fixture.Client.StartSessionInitialization(new());
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        Assert.Null(connection.Capabilities);
        Assert.Equal(new[] { "POST /api/sessions" }, fixture.Trace.ToArray());
        Assert.False(connection.Completion.IsCompleted);
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.Equal(new[] { "POST /api/sessions" }, fixture.Trace.ToArray());
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("services")]
    [InlineData("initialize")]
    [InlineData("prepare")]
    public async Task FailureAfterAcceptanceKeepsOriginalSessionAndExceptionWithoutRollback(string stage)
    {
        using var fixture = new Fixture();
        var failure = new InvalidOperationException("synthetic " + stage);
        var services = new Services((_, _) => stage == "services" ? Task.FromException(failure) : Task.CompletedTask);
        fixture.Handler.Respond = (request, _) => stage == "initialize" && request.Path.EndsWith("/initialize", StringComparison.Ordinal)
            ? Task.FromException<HttpResponseMessage>(failure) : Task.FromResult(fixture.Handler.Default(request));
        using var initialization = fixture.Client.StartSessionInitialization(new() { ResumeSessionId = "original" }, new()
        {
            SessionConfirmedAsync = (_, _) => stage == "confirmed" ? Task.FromException(failure) : Task.CompletedTask,
            RequiredServices = services,
            PrepareObservationAsync = (_, _) => stage == "prepare"
                ? Task.FromException<SessionObservationOptions?>(failure) : Task.FromResult<SessionObservationOptions?>(Observation())
        });
        var accepted = await initialization.Accepted.WaitAsync(Deadline);
        Assert.Equal("original", accepted.Id);
        Assert.Same(failure, await Record.ExceptionAsync(() => initialization.Ready.WaitAsync(Deadline)));
        Assert.Same(accepted, await initialization.Accepted);
        Assert.Equal(SessionInitializationStage.Failed, initialization.Progress.Stage);
        Assert.Equal(stage == "confirmed" ? 0 : 1, services.Stops);
        Assert.Single(fixture.Handler.Requests, request => request.Path == "/api/sessions");
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal));
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceTheOriginalFailureOrAcceptedId()
    {
        using var fixture = new Fixture();
        var failure = new InvalidOperationException("synthetic start");
        var cleanup = new IOException("synthetic cleanup");
        var services = new Services((_, _) => Task.FromException(failure), () => Task.FromException(cleanup));
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new() { RequiredServices = services });
        var session = await initialization.Accepted.WaitAsync(Deadline);
        Assert.Same(failure, await Record.ExceptionAsync(() => initialization.Ready.WaitAsync(Deadline)));
        Assert.Same(cleanup, initialization.CleanupFailure);
        Assert.Same(session, await initialization.Accepted);
        Assert.Equal(1, services.Stops);
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task CleanupBudgetBoundsFailedStartupWithoutReplacingFailureOrClaimingStopped()
    {
        using var fixture = new Fixture();
        var failure = new InvalidOperationException("synthetic start");
        var stopEntered = Signal(); var stopRelease = Signal(); var stopFinished = Signal();
        var services = new Services((_, _) => Task.FromException(failure), async () =>
        {
            stopEntered.TrySetResult();
            await stopRelease.Task;
            stopFinished.TrySetResult();
        });
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        { RequiredServices = services, CleanupTimeout = TimeSpan.FromMilliseconds(30) });
        try
        {
            await stopEntered.Task.WaitAsync(Deadline);
            var accepted = await initialization.Accepted.WaitAsync(Deadline);
            Assert.Same(failure, await Record.ExceptionAsync(() => initialization.Ready.WaitAsync(Deadline)));
            var cleanup = Assert.IsType<TimeoutException>(initialization.CleanupFailure);
            Assert.Equal("session_cleanup_unconfirmed", cleanup.Message);
            Assert.Same(accepted, await initialization.Accepted);
            Assert.Equal(SessionInitializationStage.Failed, initialization.Progress.Stage);
            Assert.Equal(1, services.Stops);
            Assert.False(stopFinished.Task.IsCompleted);
            Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/initialize", StringComparison.Ordinal));
            AssertNoBusinessRequests(fixture);
        }
        finally
        {
            // 超时只结束等待；放开底层收尾，不能将未确认资源写成已停止。
            stopRelease.TrySetResult();
            await stopFinished.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task CleanupBudgetBoundsConnectionStopWithoutRemoteDeleteOrClaimingStopped()
    {
        using var fixture = new Fixture();
        var stopEntered = Signal(); var stopRelease = Signal(); var stopFinished = Signal();
        var services = new Services(stop: async () =>
        {
            stopEntered.TrySetResult();
            await stopRelease.Task;
            stopFinished.TrySetResult();
        });
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        { RequiredServices = services, CleanupTimeout = TimeSpan.FromMilliseconds(30) });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        var requestCount = fixture.Handler.Requests.Count;
        try
        {
            var stopping = connection.StopAsync();
            await stopEntered.Task.WaitAsync(Deadline);
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => stopping.WaitAsync(Deadline));
            Assert.Equal("session_cleanup_unconfirmed", failure.Message);
            Assert.Same(failure, await Record.ExceptionAsync(() => connection.Completion.WaitAsync(Deadline)));
            Assert.Same(connection, await initialization.Ready);
            Assert.Null(initialization.CleanupFailure);
            Assert.Equal(1, services.Stops);
            Assert.False(stopFinished.Task.IsCompleted);
            Assert.Equal(requestCount, fixture.Handler.Requests.Count);
            AssertNoBusinessRequests(fixture);
        }
        finally
        {
            stopRelease.TrySetResult();
            await stopFinished.Task.WaitAsync(Deadline);
        }
        Assert.True(connection.Completion.IsFaulted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60001)]
    public void InvalidCleanupBudgetIsRejectedBeforeTokenOrRequests(int milliseconds)
    {
        var tokenCalls = 0;
        using var fixture = new Fixture(tokenProvider: _ =>
        { Interlocked.Increment(ref tokenCalls); return Task.FromResult("synthetic-token"); });
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Client.StartSessionInitialization(
            new() { RequestId = "session" }, new() { CleanupTimeout = TimeSpan.FromMilliseconds(milliseconds) }));
        Assert.Equal(0, tokenCalls);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task CancellationBeforeStartingMakesNoRequest()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var services = new Services();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new() { RequiredServices = services }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Accepted.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(0, services.Starts); Assert.Equal(0, services.Stops);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("services")]
    [InlineData("headers")]
    public async Task CancellationStopsLaterStagesAndOnlyCleansResourcesStartedByThisAttempt(string stage)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        async Task Block(CancellationToken token) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        var services = new Services((_, token) => stage == "services" ? Block(token) : Task.CompletedTask);
        fixture.Handler.Respond = async (request, token) =>
        {
            if (stage == "headers" && request.Path.EndsWith("/events", StringComparison.Ordinal)) await Block(token);
            return fixture.Handler.Default(request);
        };
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            SessionConfirmedAsync = (_, token) => stage == "confirmed" ? Block(token) : Task.CompletedTask,
            RequiredServices = services,
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation())
        }, cancellation.Token);
        var accepted = await initialization.Accepted.WaitAsync(Deadline);
        await entered.Task.WaitAsync(Deadline); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
        Assert.Same(accepted, await initialization.Accepted);
        Assert.Equal(SessionInitializationStage.Canceled, initialization.Progress.Stage);
        Assert.Equal(stage == "confirmed" ? 0 : 1, services.Stops);
        if (stage != "headers")
            Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/initialize", StringComparison.Ordinal));
        AssertNoBusinessRequests(fixture);
    }

    [Theory]
    [InlineData("confirmed", false)]
    [InlineData("services", false)]
    [InlineData("prepare", false)]
    [InlineData("services", true)]
    public async Task IgnoredPreparationCancellationIsBoundedAndLateServicesAreStopped(string stage, bool lateFailure)
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal(); var returned = Signal(); var stopped = Signal();
        var preparationCalls = 0;
        CancellationToken preparationToken = default;
        async Task IgnoreCancellation(CancellationToken token)
        {
            preparationToken = token; entered.TrySetResult();
            await release.Task;
            try { if (lateFailure) throw new InvalidOperationException("synthetic late preparation"); }
            finally { returned.TrySetResult(); }
        }
        var services = new Services((_, token) => stage == "services" ? IgnoreCancellation(token) : Task.CompletedTask,
            () => { stopped.TrySetResult(); return Task.CompletedTask; });
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(30),
            SessionConfirmedAsync = (_, token) => stage == "confirmed" ? IgnoreCancellation(token) : Task.CompletedTask,
            RequiredServices = services,
            PrepareObservationAsync = async (_, token) =>
            {
                Interlocked.Increment(ref preparationCalls);
                if (stage == "prepare") await IgnoreCancellation(token);
                return Observation();
            }
        });
        await entered.Task.WaitAsync(Deadline);
        try
        {
            var accepted = await initialization.Accepted.WaitAsync(Deadline);
            initialization.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
            Assert.Equal("session_cleanup_unconfirmed", Assert.IsType<TimeoutException>(initialization.CleanupFailure).Message);
            Assert.Same(accepted, await initialization.Accepted);
            Assert.Equal(SessionInitializationStage.Canceled, initialization.Progress.Stage);
            Assert.False(returned.Task.IsCompleted);
            Assert.Equal(0, services.Stops);
            // 原委托仍在运行，已取消的源此刻不能提前释放。
            Assert.True(preparationToken.WaitHandle.WaitOne(0));
            Assert.Equal(stage == "prepare" ? 1 : 0, preparationCalls);
            Assert.Equal(stage == "prepare" ? 1 : 0, fixture.Handler.Requests.Count(request => request.Path.EndsWith("/initialize", StringComparison.Ordinal)));
            var requests = fixture.Handler.Requests.Count;
            release.TrySetResult();
            await returned.Task.WaitAsync(Deadline);
            if (stage != "confirmed") await stopped.Task.WaitAsync(Deadline);
            Assert.Equal(stage == "confirmed" ? 0 : 1, services.Starts);
            Assert.Equal(stage == "confirmed" ? 0 : 1, services.Stops);
            Assert.Equal(requests, fixture.Handler.Requests.Count);
            Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal));
            AssertNoBusinessRequests(fixture);
        }
        finally
        {
            initialization.Cancel(); release.TrySetResult();
            await returned.Task.WaitAsync(Deadline);
            if (stage != "confirmed") await stopped.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task IgnoredObserverCancellationKeepsPumpPendingUntilLateFailureAfterCleanupBudget()
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal(); var returned = Signal();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(30),
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation(async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                try { throw new InvalidOperationException("synthetic late observer"); }
                finally { returned.TrySetResult(); }
            }))
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        var feed = fixture.Handler.Feed("session");
        feed.Push("session", 1);
        await entered.Task.WaitAsync(Deadline);
        try
        {
            var requests = fixture.Handler.Requests.Count;
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => connection.StopAsync().WaitAsync(Deadline));
            Assert.Equal("session_cleanup_unconfirmed", failure.Message);
            Assert.False(returned.Task.IsCompleted);
            // 传输取消会先关闭流，但这不代表忽略取消的观察回调已经退出。
            Assert.True(feed.Disposed.Task.IsCompleted);
            release.TrySetResult();
            await returned.Task.WaitAsync(Deadline);
            await feed.Disposed.Task.WaitAsync(Deadline);
            Assert.Same(failure, await Record.ExceptionAsync(() => connection.Completion.WaitAsync(Deadline)));
            Assert.Equal(requests, fixture.Handler.Requests.Count);
            AssertNoBusinessRequests(fixture);
        }
        finally
        {
            release.TrySetResult();
            await returned.Task.WaitAsync(Deadline);
            await feed.Disposed.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task StartupCancellationAndDisposalAfterReadyCannotStopTheTransferredStream()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var observed = Signal(); var services = new Services();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            RequiredServices = services,
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation((_, _) => { observed.TrySetResult(); return Task.CompletedTask; }))
        }, cancellation.Token);
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        cancellation.Cancel(); initialization.Cancel(); initialization.Dispose();
        Assert.False(services.Lifetime.IsCancellationRequested);
        fixture.Handler.Feed("session").Push("session", 1);
        await observed.Task.WaitAsync(Deadline);
        Assert.False(connection.Completion.IsCompleted);
        Assert.Equal(0, services.Stops);
        await connection.StopAsync().WaitAsync(Deadline);
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.Equal(1, services.Stops);
        Assert.True(services.Lifetime.IsCancellationRequested);
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task CancellationInsideReadyProgressCannotStopTransferredServicesOrObservation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var confirmRelease = Signal(); var canceledFromReady = Signal();
        var observed = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new Services();
        SessionInitialization? operation = null;
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            // 首次准备保持在门后，保证回调取消的是已经交还的同一个初始化句柄。
            SessionConfirmedAsync = (_, token) => confirmRelease.Task.WaitAsync(token),
            RequiredServices = services,
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation((_, token) =>
            { observed.TrySetResult(token); return Task.CompletedTask; })),
            Progress = progress =>
            {
                if (progress.Stage != SessionInitializationStage.Ready) return;
                cancellation.Cancel();
                operation!.Cancel();
                canceledFromReady.TrySetResult();
            }
        }, cancellation.Token);
        operation = initialization;
        confirmRelease.SetResult();
        await canceledFromReady.Task.WaitAsync(Deadline);
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(services.Lifetime.IsCancellationRequested);
        Assert.Equal(0, services.Stops);
        fixture.Handler.Feed("session").Push("session", 1);
        var observationToken = await observed.Task.WaitAsync(Deadline);
        Assert.False(observationToken.IsCancellationRequested);
        Assert.False(connection.Completion.IsCompleted);
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.True(services.Lifetime.IsCancellationRequested);
        Assert.True(observationToken.IsCancellationRequested);
        Assert.Equal(1, services.Stops);
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task ObserverCancellationIsAnOriginalFailureNotAnImplicitLocalStop()
    {
        using var fixture = new Fixture();
        var failure = new OperationCanceledException("synthetic observer cancellation");
        var services = new Services();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            RequiredServices = services,
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation((_, _) => Task.FromException(failure)))
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        fixture.Handler.Feed("session").Push("session", 1);
        Assert.Same(failure, await Record.ExceptionAsync(() => connection.Completion.WaitAsync(Deadline)));
        Assert.Equal(1, services.Stops);
        Assert.Single(fixture.Handler.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedEventTransportIsObservedAndReleasesRequiredServices()
    {
        using var fixture = new Fixture(); var services = new Services();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            RequiredServices = services,
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation())
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        fixture.Handler.Feed("session").End();
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => connection.Completion.WaitAsync(Deadline));
        Assert.Equal("event_stream_disconnected", error.Code);
        Assert.Equal(1, services.Stops);
        Assert.Single(fixture.Handler.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThrowingProgressObserverCannotChangeSuccessfulInitializationAndStagesStayOrdered()
    {
        using var fixture = new Fixture(); var stages = new ConcurrentQueue<SessionInitializationProgress>();
        var reported = Signal();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            Progress = item =>
            {
                stages.Enqueue(item);
                if (item.Stage == SessionInitializationStage.Ready) reported.TrySetResult();
                throw new InvalidOperationException("synthetic progress observer");
            }
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        await reported.Task.WaitAsync(Deadline);
        var values = stages.ToArray();
        Assert.Equal(new[] { SessionInitializationStage.Connecting, SessionInitializationStage.SessionConfirmed,
            SessionInitializationStage.InitializingCapabilities, SessionInitializationStage.Ready }, values.Select(value => value.Stage));
        Assert.True(values.Zip(values.Skip(1), (left, right) => left.Elapsed <= right.Elapsed).All(value => value));
        Assert.False(initialization.Progress.CanCancel);
        Assert.Null(initialization.CleanupFailure);
        await connection.StopAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedToolsPreserveOmissionVersusExplicitEmptySelection(bool empty)
    {
        using var fixture = new Fixture();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        { RequestedTools = empty ? Array.Empty<string>() : null });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        var request = Assert.Single(fixture.Handler.Requests, value => value.Path.EndsWith("/initialize", StringComparison.Ordinal));
        var body = request.Body!.Value;
        Assert.Equal(empty, body.TryGetProperty("requestedTools", out var tools));
        if (empty) { Assert.Equal(JsonValueKind.Array, tools.ValueKind); Assert.Equal(0, tools.GetArrayLength()); }
        Assert.Equal("csharp", body.GetProperty("platform").GetProperty("language").GetString());
        await connection.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task MutableRequestAndInitializationOptionsAreCapturedBeforeAsynchronousWork()
    {
        var entered = Signal(); var release = Signal();
        using var fixture = new Fixture(tokenProvider: async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "synthetic-token"; });
        var labels = new Dictionary<string, string> { ["tag"] = "original" };
        var tools = new List<string> { "Read" };
        var request = new CreateSessionOptions { RequestId = "original-request", Labels = labels };
        var options = new SessionInitializationOptions { RequestedTools = tools };
        using var initialization = fixture.Client.StartSessionInitialization(request, options);
        await entered.Task.WaitAsync(Deadline);
        request.RequestId = "replacement"; labels["tag"] = "replacement"; tools[0] = "Write";
        options.RequestedTools = Array.Empty<string>();
        release.SetResult();
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        var created = Assert.Single(fixture.Handler.Requests, value => value.Path == "/api/sessions");
        Assert.Equal("original-request", created.Body!.Value.GetProperty("requestId").GetString());
        Assert.Equal("original", created.Body.Value.GetProperty("labels").GetProperty("tag").GetString());
        var initialized = Assert.Single(fixture.Handler.Requests, value => value.Path.EndsWith("/initialize", StringComparison.Ordinal));
        Assert.Equal(new[] { "Read" }, initialized.Body!.Value.GetProperty("requestedTools").EnumerateArray().Select(item => item.GetString()));
        await connection.StopAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("scope")]
    public async Task IdentityChangesAfterAcceptancePreventServicesAndInitialization(string changed)
    {
        using var fixture = new Fixture(); var services = new Services();
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            SessionConfirmedAsync = (_, _) =>
            {
                if (changed == "principal") fixture.Principal = "app/another";
                else fixture.AuthorizationRevision = "3";
                return Task.CompletedTask;
            },
            RequiredServices = services
        });
        var accepted = await initialization.Accepted.WaitAsync(Deadline);
        await Assert.ThrowsAsync<TansrProtocolException>(() => initialization.Ready.WaitAsync(Deadline));
        Assert.Same(accepted, await initialization.Accepted);
        Assert.Equal(0, services.Starts); Assert.Equal(0, services.Stops);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/initialize", StringComparison.Ordinal));
        AssertNoBusinessRequests(fixture);
    }

    [Fact]
    public async Task ConcurrentInitializationsOwnSeparateCancellationAndServices()
    {
        using var fixture = new Fixture();
        var firstEntered = Signal(); var firstServices = new Services(async (_, token) =>
        { firstEntered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        var secondServices = new Services();
        using var first = fixture.Client.StartSessionInitialization(new() { RequestId = "first" }, new() { RequiredServices = firstServices });
        await firstEntered.Task.WaitAsync(Deadline);
        using var second = fixture.Client.StartSessionInitialization(new() { RequestId = "second" }, new() { RequiredServices = secondServices });
        using var connection = await second.Ready.WaitAsync(Deadline);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Ready.WaitAsync(Deadline));
        Assert.Equal("first", (await first.Accepted).Id);
        Assert.Equal("second", connection.Session.Id);
        Assert.Equal(1, firstServices.Stops); Assert.Equal(0, secondServices.Stops);
        Assert.False(secondServices.Lifetime.IsCancellationRequested);
        Assert.False(connection.Completion.IsCompleted);
        await connection.StopAsync().WaitAsync(Deadline);
        Assert.Equal(1, secondServices.Stops);
        AssertNoBusinessRequests(fixture);
    }

    private static bool ContainsException(Exception? error, Exception expected) => ReferenceEquals(error, expected) ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => ContainsException(inner, expected)) ||
        error?.InnerException != null && ContainsException(error.InnerException, expected);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SessionObservationOptions Observation(Func<AgentEvent, CancellationToken, Task>? observer = null) =>
        new(observer ?? ((_, _) => Task.CompletedTask)) { Events = new EventStreamOptions { Reconnect = false } };
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static HttpResponseMessage Response(JsonElement value) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(WireJson.EncodeControl(value)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } };
    private static void AssertNoBusinessRequests(Fixture fixture) => Assert.All(fixture.Handler.Requests, request =>
        Assert.True(request.Path == "/api/capabilities/sessions" || request.Method == "POST" && request.Path == "/api/sessions" ||
            request.Method == "POST" && request.Path.EndsWith("/initialize", StringComparison.Ordinal) ||
            request.Method == "GET" && request.Path.EndsWith("/events", StringComparison.Ordinal), request.Method + " " + request.Path));

    private sealed class PersistenceLease(Func<Task>? stop = null) : ISessionPersistenceLease
    {
        internal int Stops;
        internal readonly TaskCompletionSource Stopped = Signal();
        public Task StopAsync()
        {
            Interlocked.Increment(ref Stops); Stopped.TrySetResult();
            return stop?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class Services(Func<AgentSession, CancellationToken, Task>? start = null, Func<Task>? stop = null) : ISessionInitializationServices
    {
        internal int Starts, Stops;
        internal CancellationToken Lifetime;
        public Task StartAsync(AgentSession session, CancellationToken lifetimeToken)
        { Interlocked.Increment(ref Starts); Lifetime = lifetimeToken; return start?.Invoke(session, lifetimeToken) ?? Task.CompletedTask; }
        public Task StopAsync() { Interlocked.Increment(ref Stops); return stop?.Invoke() ?? Task.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ConcurrentQueue<string> Trace = new();
        internal readonly Handler Handler;
        internal readonly HttpClient Http;
        internal readonly TansrClient Client;
        internal string Principal = "app/user", AuthorizationRevision = "2";
        internal Fixture(SessionContract contract = SessionContract.Sdk2OffloadV1, Func<CancellationToken, Task<string>>? tokenProvider = null)
        {
            Handler = new Handler(Trace, contract); Http = UnifiedStamp.Client(Handler);
            Client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("https://synthetic.invalid/"),
                SessionContract = contract,
                TokenProvider = tokenProvider ?? (_ => Task.FromResult("synthetic-token")),
                PrincipalProvider = () => Principal,
                ExecutionScopeProvider = () => Json(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = AuthorizationRevision }),
                MaxReconnectAttempts = 0,
                ReconnectDelay = TimeSpan.Zero
            }, Http);
        }
        public void Dispose() { Client.Dispose(); Http.Dispose(); }
    }

    [Fact]
    public async Task BoundedRecoveryInstallsBeforeOpeningTheSingleEventStream()
    {
        using var fixture = new Fixture(SessionContract.Sdk1);
        using var view = new Tansr.Sdk.Views.SessionView();
        SessionRecovery? recovery = null;
        fixture.Handler.Respond = (request, _) => Task.FromResult(request.Path.EndsWith("/history", StringComparison.Ordinal)
            ? Response(Json(Tansr.Sdk.Tests.Examples.ExampleEventRecoveryTests.Projection(0))) : fixture.Handler.Default(request));
        using var initialization = fixture.Client.StartSessionInitialization(new CreateSessionOptions { ResumeSessionId = "s" },
            new SessionInitializationOptions { PrepareObservationAsync = (session, _) =>
            {
                recovery = new SessionRecovery(session, view);
                return Task.FromResult<SessionObservationOptions?>(new SessionObservationOptions((item, _) => { view.Apply(item); return Task.CompletedTask; })
                { Recovery = recovery });
            } });
        var connection = await initialization.Ready;
        Assert.False(recovery!.Required);
        Assert.Equal(10, view.Snapshot.LastSequence);
        var requests = fixture.Handler.Requests.ToArray();
        Assert.Equal(new[] { "/api/sessions", "/api/sessions/s/history", "/api/sessions/s/events" }, requests.Select(x => x.Path));
        Assert.Contains("projection=recovery", requests[1].Query);
        Assert.Equal("10", requests[2].LastEventId);
        await connection.StopAsync();
    }

    [Fact]
    public async Task PendingRecoveryStillCompletesObservationReadyAndDeliversCurrentApprovals()
    {
        using var fixture = new Fixture(SessionContract.Sdk1);
        using var view = new Tansr.Sdk.Views.SessionView();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionRecovery? recovery = null;
        fixture.Handler.Respond = (request, _) => request.Path.EndsWith("/history", StringComparison.Ordinal)
            ? throw new TansrHttpException(409, "recovery_projection_pending") : Task.FromResult(fixture.Handler.Default(request));
        using var initialization = fixture.Client.StartSessionInitialization(new CreateSessionOptions { ResumeSessionId = "s" },
            new SessionInitializationOptions { PrepareObservationAsync = (session, _) =>
            {
                recovery = new SessionRecovery(session, view);
                return Task.FromResult<SessionObservationOptions?>(new SessionObservationOptions((item, _) =>
                { view.Apply(item); received.TrySetResult(); return Task.CompletedTask; }) { Recovery = recovery });
            } });
        var connection = await initialization.Ready;
        Assert.True(recovery!.Required);
        fixture.Handler.Feed("s").PushFrame("event: server.permission.request\nid: 2\ndata: {\"ts\":1,\"requestId\":\"fresh\",\"digest\":\"d\"}\n\n");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(view.Snapshot.PendingRequests);
        Assert.Single(fixture.Handler.Requests, request => request.Path.EndsWith("/history", StringComparison.Ordinal));
        Assert.True(recovery.Required);
        await connection.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCallbackIgnoringCancellationUsesBoundedPreparationCleanup(bool afterInstall)
    {
        using var fixture = new Fixture(SessionContract.Sdk1);
        using var view = new Tansr.Sdk.Views.SessionView();
        var entered = Signal(); var release = Signal(); var stopped = Signal();
        var services = new Services(stop: () => { stopped.TrySetResult(); return Task.CompletedTask; });
        async Task IgnoreCancellation()
        { entered.TrySetResult(); await release.Task; }
        fixture.Handler.Respond = (request, _) => Task.FromResult(request.Path.EndsWith("/history", StringComparison.Ordinal)
            ? Response(Examples.ExampleEventRecoveryTests.Projection(0).Deserialize<JsonElement>()) : fixture.Handler.Default(request));
        using var initialization = fixture.Client.StartSessionInitialization(new CreateSessionOptions { ResumeSessionId = "s" }, new()
        {
            CleanupTimeout = TimeSpan.FromMilliseconds(30), RequiredServices = services,
            PrepareObservationAsync = (session, _) => Task.FromResult<SessionObservationOptions?>(new SessionObservationOptions((_, _) => Task.CompletedTask)
            {
                Recovery = new SessionRecovery(session, view,
                    installed: (_, _) => afterInstall ? IgnoreCancellation() : Task.CompletedTask,
                    changed: (_, _) => afterInstall ? Task.CompletedTask : IgnoreCancellation())
            })
        });
        await entered.Task.WaitAsync(Deadline);
        try
        {
            var accepted = await initialization.Accepted;
            initialization.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
            Assert.Same(accepted, await initialization.Accepted);
            Assert.Equal("session_cleanup_unconfirmed", Assert.IsType<TimeoutException>(initialization.CleanupFailure).Message);
            Assert.Equal(0, services.Stops);
            Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal));
            release.TrySetResult();
            await stopped.Task.WaitAsync(Deadline);
            Assert.Equal(1, services.Stops);
            Assert.All(fixture.Handler.Requests, request => Assert.True(
                request.Method == "POST" && request.Path == "/api/sessions" ||
                request.Method == "GET" && request.Path == "/api/sessions/s/history" && request.Query.Contains("projection=recovery")));
        }
        finally { release.TrySetResult(); await stopped.Task.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task RecoveryCannotBindAnotherSessionInstanceWithTheSameId()
    {
        using var fixture = new Fixture(SessionContract.Sdk1);
        using var foreign = new Fixture(SessionContract.Sdk1);
        using var view = new Tansr.Sdk.Views.SessionView();
        var unrelated = new AgentSession(foreign.Client, "s", 0, true);
        using var initialization = fixture.Client.StartSessionInitialization(new CreateSessionOptions { ResumeSessionId = "s" }, new()
        {
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(new SessionObservationOptions((_, _) => Task.CompletedTask)
            { Recovery = new SessionRecovery(unrelated, view) })
        });
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => initialization.Ready.WaitAsync(Deadline));
        Assert.Equal("recovery_session_mismatch", error.Code);
        Assert.Equal("s", (await initialization.Accepted).Id);
        Assert.Empty(foreign.Handler.Requests);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Path.EndsWith("/history", StringComparison.Ordinal) || request.Path.EndsWith("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObserverTimeoutDoesNotMasqueradeAsCleanupBudgetOrHideDistinctCleanupFailure()
    {
        using var fixture = new Fixture();
        var entered = Signal(); var release = Signal(); var stopping = Signal(); var stopRelease = Signal();
        var observerFailure = new TimeoutException("session_cleanup_unconfirmed");
        var cleanupFailure = new TimeoutException("session_cleanup_unconfirmed");
        var services = new Services(stop: async () => { stopping.TrySetResult(); await stopRelease.Task; throw cleanupFailure; });
        using var initialization = fixture.Client.StartSessionInitialization(new() { RequestId = "session" }, new()
        {
            RequiredServices = services, CleanupTimeout = TimeSpan.FromSeconds(3),
            PrepareObservationAsync = (_, _) => Task.FromResult<SessionObservationOptions?>(Observation(async (_, _) =>
            { entered.TrySetResult(); await release.Task; throw observerFailure; }))
        });
        using var connection = await initialization.Ready.WaitAsync(Deadline);
        fixture.Handler.Feed("session").Push("session", 1);
        await entered.Task.WaitAsync(Deadline);
        try
        {
            var stop = connection.StopAsync();
            release.TrySetResult();
            await stopping.Task.WaitAsync(Deadline);
            Assert.False(stop.IsCompleted); // a producer timeout is not expiration of our cleanup budget
            stopRelease.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(Deadline));
            Assert.Contains(observerFailure, failure.InnerExceptions);
            Assert.Contains(cleanupFailure, failure.InnerExceptions);
            Assert.Equal(1, services.Stops);
        }
        finally { release.TrySetResult(); stopRelease.TrySetResult(); }
    }

    private sealed record Request(string Method, string Path, JsonElement? Body, string Query = "", string? LastEventId = null);
    private sealed class Handler(ConcurrentQueue<string> trace, SessionContract contract) : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<Request> Requests = new();
        private readonly ConcurrentDictionary<string, FeedStream> feeds = new();
        internal Func<Request, CancellationToken, Task<HttpResponseMessage>>? Respond;
        internal FeedStream Feed(string session) => feeds.GetOrAdd(session, _ => new FeedStream());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonElement? body = null;
            if (request.Content != null)
            {
                using var document = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                body = document.RootElement.Clone();
            }
            var item = new Request(request.Method.Method, request.RequestUri!.AbsolutePath, body, request.RequestUri.Query,
                request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null);
            Requests.Enqueue(item); trace.Enqueue(item.Method + " " + item.Path);
            if (item.Path.EndsWith("/initialize", StringComparison.Ordinal))
                Assert.Equal("/api/sessions/" + Uri.EscapeDataString(item.Body!.Value.GetProperty("sessionId").GetString()!) + "/initialize", item.Path);
            return Respond == null ? Default(item) : await Respond(item, cancellationToken);
        }
        internal HttpResponseMessage Default(Request request)
        {
            if (request.Path == "/api/capabilities/sessions")
                return Response(Json(new { protocol = "sdk2-ext-v1", contracts = new[] { new { contract = "sdk2-offload-v1", availability = "source-required" } } }));
            if (request.Path == "/api/sessions" && request.Method == "POST")
            {
                var body = request.Body!.Value;
                var resumed = body.TryGetProperty("resume", out var resume);
                var id = resumed ? resume.GetProperty("sessionId").GetString() : body.TryGetProperty("requestId", out var key) ? key.GetString() : "session";
                return contract == SessionContract.Sdk1 ? Response(Json(new { sessionId = id, resumed, lastSeq = 0 })) :
                    Response(Json(new { availability = "source-required", contract = "sdk2-offload-v1", sessionId = id, resumed, lastSeq = 0 }));
            }
            if (request.Path.EndsWith("/initialize", StringComparison.Ordinal) && request.Method == "POST")
            {
                var body = request.Body!.Value;
                return Response(Json(new
                {
                    protocol = "sdk2-ext-v1",
                    sessionId = body.GetProperty("sessionId").GetString(),
                    platform = body.GetProperty("platform"),
                    capabilityRevision = new string('a', 64),
                    effectiveTools = Array.Empty<object>(),
                    binding = (object?)null
                }));
            }
            if (request.Path.EndsWith("/events", StringComparison.Ordinal) && request.Method == "GET")
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(Feed(request.Path.Split('/')[3])) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
            throw new InvalidOperationException("Unexpected synthetic request: " + request.Method + " " + request.Path);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) foreach (var feed in feeds.Values) feed.Dispose(); base.Dispose(disposing); }
    }

    // 首帧由测试显式放行；取消及关闭唤醒读取，测试无需真实网络或等待固定延时。
    private sealed class FeedStream : Stream
    {
        private readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>();
        internal readonly TaskCompletionSource Disposed = Signal();
        private byte[]? current;
        private int offset;
        internal void Push(string session, int sequence) => Assert.True(queue.Writer.TryWrite(Encoding.UTF8.GetBytes(
            "id: " + sequence + "\ndata: " + Json(new { sessionId = session, seq = sequence, type = "assistant.delta", text = "synthetic" }).GetRawText() + "\n\n")));
        internal void End() => queue.Writer.TryComplete();
        internal void PushFrame(string frame) => Assert.True(queue.Writer.TryWrite(Encoding.UTF8.GetBytes(frame)));
        public override async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellationToken)
        {
            while (current == null || offset == current.Length)
            {
                if (!await queue.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!queue.Reader.TryRead(out current)) continue;
                offset = 0;
            }
            int length = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, start, length); offset += length; return length;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { queue.Writer.TryComplete(); Disposed.TrySetResult(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
