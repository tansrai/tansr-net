using Tansr.Examples;

namespace Tansr.Sdk.Tests.Examples;

public sealed class LocalConversationStateTests
{
    [Fact]
    public void ReopenKeepsFullDraftPresentationAndFixedInputWithoutAnyTokenField()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tansr-example-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "state.json"); var state = new LocalConversationState(path);
            var text = new string('文', 70000) + "🙂\n";
            state.Save("https://serve.example", "s", text, "只读显示", "{\"messages\":[]}");
            state.SaveInput(new TurnInputRecord("s", "input", "epoch", "turn", text, "unconfirmed", null));
            var reopened = new LocalConversationState(path).Load();
            Assert.Equal(text, reopened.Draft); Assert.Equal(text, reopened.Input!.Text); Assert.Equal("unconfirmed", reopened.Input.Outcome);
            Assert.Contains("不会自动回灌模型", reopened.OfflineText); Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewSessionDoesNotImportOldHistoryOrInputAndSecretUrlsCannotBePersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tansr-example-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new LocalConversationState(Path.Combine(directory, "state.json"));
            state.Save("https://serve.example", "one", "draft", "view", "old history");
            state.SaveInput(new TurnInputRecord("one", "input", "epoch", "turn", "text", "unconfirmed", null));
            Assert.Throws<InvalidOperationException>(() => state.Save("https://serve.example", "two", "new draft", "new view"));
            Assert.Equal("one", state.Snapshot.SessionId); Assert.Equal("input", state.Snapshot.Input!.InputId); Assert.Equal("old history", state.Snapshot.HistoryJson);
            state.SaveInput(new TurnInputRecord("one", "input", "epoch", "turn", "text", "accepted", "accepted original receipt"));
            state.Save("https://serve.example", "two", "new draft", "new view");
            Assert.Null(state.Snapshot.HistoryJson); Assert.Null(state.Snapshot.Input);
            Assert.Throws<InvalidOperationException>(() => state.Save("https://user:password@serve.example", "two", "", ""));
            Assert.Throws<InvalidOperationException>(() => state.Save("https://serve.example/?token=secret", "two", "", ""));
            Assert.Equal("new draft", state.Snapshot.Draft);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void StaleSecondInstanceCannotOverwriteFirstInstancesUnknownInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tansr-example-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "state.json");
            new LocalConversationState(path).Save("https://serve.example", "s", "draft", "view");
            var first = new LocalConversationState(path); var second = new LocalConversationState(path);
            first.Load(); second.Load();
            first.SaveInput(new TurnInputRecord("s", "original-input", "epoch", "turn", "full original", "unconfirmed", null));
            var written = File.ReadAllBytes(path);
            var error = Assert.Throws<InvalidOperationException>(() => second.Save("https://serve.example", "s", "stale changed draft", "stale view"));
            Assert.Equal("local_state_changed_by_another_instance", error.Message); Assert.Equal(written, File.ReadAllBytes(path));
            var reopened = new LocalConversationState(path).Load();
            Assert.Equal("original-input", reopened.Input!.InputId); Assert.Equal("unconfirmed", reopened.Input.Outcome); Assert.Equal("full original", reopened.Input.Text);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void TrustedUsersAtTheSameEndpointHaveSeparateDraftHistoryAndOriginalInput()
    {
        using var fixture = new ContextFixture();
        var first = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A");
        first.Save(ContextFixture.Endpoint, "session-A", "draft-A", "presentation-A", "history-A");
        first.SaveInput(new TurnInputRecord("session-A", "input-A", "epoch-A", "turn-A", "input-A-body", "unconfirmed", null));
        var original = File.ReadAllBytes(first.PathName);

        var second = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-B");
        Assert.True(first.HasTrustedIdentity); Assert.True(second.HasTrustedIdentity);
        Assert.NotEqual(first.PathName, second.PathName);
        AssertEmpty(second.Load());
        second.Save(ContextFixture.Endpoint, "session-B", "draft-B", "presentation-B", "history-B");
        Assert.Equal(original, File.ReadAllBytes(first.PathName));

        var recovered = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A").Load();
        Assert.Equal("session-A", recovered.SessionId); Assert.Equal("draft-A", recovered.Draft);
        Assert.Equal("history-A", recovered.HistoryJson); Assert.Equal("input-A", recovered.Input!.InputId);
        Assert.Equal("unconfirmed", recovered.Input.Outcome);
        Assert.DoesNotContain("draft-B", recovered.OfflineText, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointBoundaryDoesNotAdoptTheSameUsersOtherServeConversation()
    {
        using var fixture = new ContextFixture();
        var first = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A");
        first.Save(ContextFixture.Endpoint, "session-A", "private draft", "private presentation", "private history");
        var original = File.ReadAllBytes(first.PathName);
        const string otherEndpoint = "https://other-serve.example";
        var second = LocalConversationState.ForContext(fixture.Path, otherEndpoint, () => "trusted-user-A");
        Assert.NotEqual(first.PathName, second.PathName); AssertEmpty(second.Load());
        Assert.False(first.IsCurrent(otherEndpoint));
        second.Save(otherEndpoint, "other-session", "other draft", "other presentation");
        Assert.Equal(original, File.ReadAllBytes(first.PathName));
    }

    [Fact]
    public void LateOldAccountCallbacksCannotReadOrWriteTheNewAccountsState()
    {
        using var fixture = new ContextFixture(); string currentIdentity = "trusted-user-A";
        var first = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => currentIdentity);
        first.Save(ContextFixture.Endpoint, "session-A", "draft-A", "presentation-A", "history-A");
        first.SaveInput(new TurnInputRecord("session-A", "input-A", "epoch", "turn", "original-A", "unconfirmed", null));
        Action<TurnInputRecord> oldReceiptCallback = first.SaveInput;
        var firstBytes = File.ReadAllBytes(first.PathName);

        currentIdentity = "trusted-user-B";
        var second = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => currentIdentity);
        AssertEmpty(second.Load()); second.Save(ContextFixture.Endpoint, "session-B", "draft-B", "presentation-B");
        var secondBytes = File.ReadAllBytes(second.PathName);
        Assert.False(first.IsCurrent(ContextFixture.Endpoint)); Assert.True(second.IsCurrent(ContextFixture.Endpoint));
        Assert.Equal("local_state_identity_changed", Assert.Throws<InvalidOperationException>(() => first.Load()).Message);
        Assert.Equal("local_state_identity_changed", Assert.Throws<InvalidOperationException>(() => { _ = first.Snapshot; }).Message);
        Assert.Equal("local_state_identity_changed", Assert.Throws<InvalidOperationException>(() => first.Save(ContextFixture.Endpoint, "session-A", "late draft", "late callback")).Message);
        Assert.Equal("local_state_identity_changed", Assert.Throws<InvalidOperationException>(() => oldReceiptCallback(new TurnInputRecord("session-A", "input-A", "epoch", "turn", "original-A", "accepted", "late receipt"))).Message);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.PathName)); Assert.Equal(secondBytes, File.ReadAllBytes(second.PathName));
        Assert.Equal("draft-B", second.Snapshot.Draft); Assert.Null(second.Snapshot.Input);

        currentIdentity = "trusted-user-A";
        var original = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => currentIdentity).Load();
        Assert.Equal("unconfirmed", original.Input!.Outcome); Assert.Equal("input-A", original.Input.InputId);
    }

    [Fact]
    public void MissingTrustedIdentityNeverAutomaticallyAdoptsOrDeletesLegacyUnboundFiles()
    {
        using var fixture = new ContextFixture();
        var legacy = new LocalConversationState(fixture.Path);
        legacy.Save(ContextFixture.Endpoint, "legacy-session", "legacy-secret-draft", "legacy-private-view", "legacy-history");
        legacy.SaveInput(new TurnInputRecord("legacy-session", "legacy-input", "epoch", "turn", "legacy-private-input", "unconfirmed", null));
        var original = File.ReadAllBytes(fixture.Path);

        var anonymous = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => null);
        Assert.False(anonymous.HasTrustedIdentity); Assert.NotEqual(fixture.Path, anonymous.PathName); AssertEmpty(anonymous.Load());
        anonymous.Save(ContextFixture.Endpoint, "anonymous-session", "current draft", "current presentation");
        Assert.Equal("current draft", anonymous.Snapshot.Draft);
        var nextAnonymous = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => null);
        Assert.NotEqual(anonymous.PathName, nextAnonymous.PathName); AssertEmpty(nextAnonymous.Load());
        AssertEmpty(LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A").Load());
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Equal("legacy-private-view", new LocalConversationState(fixture.Path).Load().Presentation);
    }

    [Fact]
    public void CurrentTrustedScopeCanReadItsOwnOfflineCopyWithoutRemoteConnection()
    {
        using var fixture = new ContextFixture();
        var original = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A");
        original.Save(ContextFixture.Endpoint, "original-session", "offline draft", "offline presentation", "offline history");
        var freshLocalOnly = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A");
        Assert.Equal(original.PathName, freshLocalOnly.PathName); Assert.True(freshLocalOnly.IsCurrent(ContextFixture.Endpoint));
        var saved = freshLocalOnly.Load();
        Assert.Equal("offline draft", saved.Draft); Assert.Equal("offline history", saved.HistoryJson);
        Assert.Contains("offline presentation", saved.OfflineText, StringComparison.Ordinal);
        Assert.Contains("不会自动回灌模型", saved.OfflineText, StringComparison.Ordinal);
    }

    [Fact]
    public void RenewedTicketAndAuthorizationRevisionKeepTheSameTrustedPersonsOriginalPartition()
    {
        using var fixture = new ContextFixture();
        fixture.WriteScope("principal-A", "app", "user-A", "1", "synthetic-ticket-before");
        var scope = TrustedExampleScope.FromPath(fixture.ScopePath);
        var stableIdentity = scope.ReadPresentationIdentity();
        var original = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, scope.ReadPresentationIdentity);
        original.Save(ContextFixture.Endpoint, "original-session", "kept draft", "kept presentation", "kept history");
        original.SaveInput(new TurnInputRecord("original-session", "original-input", "epoch", "turn", "original text", "unconfirmed", null));
        var originalBytes = File.ReadAllBytes(original.PathName);

        fixture.WriteScope("principal-A", "app", "user-A", "2", "synthetic-ticket-after");
        Assert.Equal(stableIdentity, scope.ReadPresentationIdentity());
        Assert.True(original.IsCurrent(ContextFixture.Endpoint));
        var renewed = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, scope.ReadPresentationIdentity);
        Assert.Equal(original.PathName, renewed.PathName);
        var kept = renewed.Load();
        Assert.Equal("kept draft", kept.Draft); Assert.Equal("kept history", kept.HistoryJson);
        Assert.Equal("original-input", kept.Input!.InputId); Assert.Equal("unconfirmed", kept.Input.Outcome);
        Assert.Equal(originalBytes, File.ReadAllBytes(original.PathName));
        Assert.DoesNotContain("synthetic-ticket-before", File.ReadAllText(original.PathName), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-ticket-after", File.ReadAllText(original.PathName), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("principal-B", "app", "user-A")]
    [InlineData("principal-A", "other-app", "user-A")]
    [InlineData("principal-A", "app", "user-B")]
    public void EachTrustedPrincipalApplicationAndEndUserBoundarySelectsANewPartition(string principal, string application, string user)
    {
        using var fixture = new ContextFixture();
        fixture.WriteScope("principal-A", "app", "user-A", "1", "synthetic-ticket");
        var scope = TrustedExampleScope.FromPath(fixture.ScopePath);
        var original = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, scope.ReadPresentationIdentity);
        original.Save(ContextFixture.Endpoint, "original-session", "private draft", "private presentation");
        var originalBytes = File.ReadAllBytes(original.PathName);
        fixture.WriteScope(principal, application, user, "1", "synthetic-ticket");
        Assert.False(original.IsCurrent(ContextFixture.Endpoint));
        var changed = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, scope.ReadPresentationIdentity);
        Assert.NotEqual(original.PathName, changed.PathName); AssertEmpty(changed.Load());
        Assert.Equal(originalBytes, File.ReadAllBytes(original.PathName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyingAnotherOwnersOrLegacyUnboundFileIntoThePartitionCannotAdoptItsContent(bool legacy)
    {
        using var fixture = new ContextFixture();
        var source = legacy ? new LocalConversationState(fixture.Path) :
            LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-A");
        source.Save(ContextFixture.Endpoint, "source-session", "private source draft", "private source presentation", "source history");
        var original = File.ReadAllBytes(source.PathName);
        var target = LocalConversationState.ForContext(fixture.Path, ContextFixture.Endpoint, () => "trusted-user-B");
        File.Copy(source.PathName, target.PathName);
        Assert.Equal("local_state_identity_mismatch", Assert.Throws<InvalidOperationException>(() => target.Load()).Message);
        AssertEmpty(target.Snapshot);
        Assert.Equal(original, File.ReadAllBytes(source.PathName)); Assert.Equal(original, File.ReadAllBytes(target.PathName));
    }

    private static void AssertEmpty(LocalConversationSnapshot value)
    {
        Assert.Empty(value.SessionId); Assert.Empty(value.Draft); Assert.Empty(value.Presentation);
        Assert.Null(value.HistoryJson); Assert.Null(value.Input);
    }

    private sealed class ContextFixture : IDisposable
    {
        internal const string Endpoint = "https://serve.example";
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-example-context-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(directory, "state.json");
        internal string ScopePath => System.IO.Path.Combine(directory, "trusted-scope.json");
        internal void WriteScope(string principal, string application, string user, string revision, string ticket)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(ScopePath, System.Text.Json.JsonSerializer.Serialize(new
            {
                principal,
                scope = new { applicationScopeId = application, endUserId = user, authorizationRevision = revision },
                sessionToken = ticket,
            }));
        }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
