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
}
