using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace WpfAssistant;

internal static class Program
{
    [STAThread]
    public static void Main() => new Application().Run(new AssistantWindow());
}

internal sealed class AssistantWindow : Window
{
    private readonly TextBox _endpoint = new() { Text = "https://localhost:8787", MinWidth = 280 };
    private readonly PasswordBox _token = new() { MinWidth = 220 };
    private readonly TextBox _resume = new() { Width = 190 };
    private readonly TextBox _model = new() { Width = 120 };
    private readonly CheckBox _loopback = new() { Content = "允许本机 HTTP" };
    private readonly TextBox _conversation = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _draft = new() { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { Text = "未连接；票据只保留在内存。", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _requests = new() { Height = 80, DisplayMemberPath = "Label" };
    private readonly StackPanel _questions = new();
    private readonly TextBlock _requestSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<QuestionEditor> _questionEditors = new();
    private readonly List<Button> _sessionButtons = new();
    private readonly ComboBox _textMode = new() { Width = 85, ItemsSource = new[] { "stream", "final" }, SelectedIndex = 0 };
    private readonly ComboBox _thinkingMode = new() { Width = 85, ItemsSource = new[] { "stream", "final", "off" }, SelectedIndex = 0 };
    private Button _connect = null!;
    private string _currentToken = "";
    private AgentSession? _session;
    private TansrClient? _client;
    private SessionView? _view;
    private IDisposable? _subscription;
    private CancellationTokenSource? _lifetime;
    private Task? _observation;
    private NativeToolHost? _nativeTools;
    private string? _closeFailure;
    private bool _busy, _closing, _closed;

    internal AssistantWindow()
    {
        Title = "Tansr · WPF 原生 SDK"; Width = 1120; Height = 820; MinWidth = 840; MinHeight = 620;
        var layout = new DockPanel { Margin = new Thickness(14) }; Content = layout;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        top.Children.Add(Row(Label("Serve 地址"), _endpoint, Label("短期票据"), _token, _loopback));
        _connect = Button("连接 / 创建", ConnectAsync, false);
        top.Children.Add(Row(Label("恢复会话 ID（可空）"), _resume, Label("新会话模型（可空）"), _model,
            _connect, Button("更新票据", () => { Volatile.Write(ref _currentToken, _token.Password); _status.Text = "已更新内存票据；下次请求使用新值。"; return Task.CompletedTask; }, false)));
        top.Children.Add(Row(Label("正文呈现"), _textMode, Label("思考呈现"), _thinkingMode,
            Button("历史", () => ShowAsync("历史", _session!.GetHistoryAsync())),
            Button("状态", () => ShowAsync("会话状态", _session!.GetMetadataAsync())),
            Button("压缩", () => ShowAsync("压缩回执", _session!.CompactAsync())),
            Button("创建快照", () => ShowAsync("快照回执", _session!.CheckpointAsync())),
            Button("快照管理", ManageCheckpointsAsync), Button("关闭会话", CloseConnectionAsync), Button("仅断开本机连接", DetachAsync)));
        top.Children.Add(new TextBlock
        {
            Text = "已接通：会话流、图片输入、取消、审批/提问、历史与快照。动态切模/思考、完整记忆管理、本地 Serve 自动启动与媒体播放仍待对应能力接线。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 7),
        });
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        bottom.Children.Add(_draft);
        bottom.Children.Add(Row(Button("发送", SendAsync), Button("发送图片与草稿", SendImageAsync),
            Button("取消当前工作", async () => { await _session!.CancelAsync(); _status.Text = "取消请求已受理，等待运行终态。"; }),
            new TextBlock { Text = "失败或结果未知时保留草稿；请先查历史，避免重复发送。", VerticalAlignment = VerticalAlignment.Center }));
        bottom.Children.Add(_status);
        var requestPanel = new StackPanel { Width = 310, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(requestPanel, Dock.Right); layout.Children.Add(requestPanel);
        requestPanel.Children.Add(Label("待处理请求（服务器终态决定是否仍有效）")); requestPanel.Children.Add(_requests);
        requestPanel.Children.Add(_requestSummary);
        requestPanel.Children.Add(Row(Button("批准", () => ApproveAsync(true)), Button("拒绝", () => ApproveAsync(false))));
        requestPanel.Children.Add(new ScrollViewer { Content = _questions, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        requestPanel.Children.Add(Button("提交问题答案", AnswerAsync));
        layout.Children.Add(_conversation);
        _requests.SelectionChanged += (_, _) => RenderRequest();
        Closing += OnClosing;
        SetEnabled();
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
    private static WrapPanel Row(params UIElement[] elements)
    { var panel = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) }; foreach (var element in elements) panel.Children.Add(element); return panel; }

    private Button Button(string text, Func<Task> action, bool needsSession = true)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3) };
        if (needsSession) _sessionButtons.Add(button);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; SetEnabled();
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = "操作取消或超时；可能已受理的工作须先查询状态。"; }
        catch (Exception error) { _status.Text = ErrorText(error); }
        finally { _busy = false; SetEnabled(); }
    }

    private void SetEnabled()
    {
        foreach (var button in _sessionButtons) button.IsEnabled = !_busy && _session != null;
        if (_connect != null) _connect.IsEnabled = !_busy && _session == null;
        _textMode.IsEnabled = _thinkingMode.IsEnabled = _session == null;
    }

    private async Task ConnectAsync()
    {
        Volatile.Write(ref _currentToken, _token.Password);
        var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = new Uri(_endpoint.Text, UriKind.Absolute),
            AllowInsecureLoopback = _loopback.IsChecked == true,
            TokenProvider = _ => Task.FromResult(Volatile.Read(ref _currentToken)),
        });
        AgentSession session;
        try { session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = Empty(_resume.Text), Model = Empty(_model.Text), ClientTools = Empty(_resume.Text) == null ? NativeToolHost.Declarations : null }); }
        catch { client.Dispose(); throw; }
        _client = client; _session = session; _resume.Text = session.Id; _closeFailure = null;
        _lifetime = new CancellationTokenSource();
        _nativeTools = new NativeToolHost(session, "Tansr.WPF", async (title, ct) =>
        {
            await Dispatcher.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); Title = title; }, DispatcherPriority.Normal, ct);
        }, code => Dispatcher.BeginInvoke(new Action(() => _status.Text = code)));
        _view = new SessionView(new SessionViewOptions
        {
            TextDelivery = _textMode.SelectedIndex == 1 ? TextDeliveryMode.Final : TextDeliveryMode.Stream,
            ThinkingDelivery = (ThinkingDeliveryMode)_thinkingMode.SelectedIndex,
        });
        _subscription = _view.Subscribe(Render, new DispatcherSynchronizationContext(Dispatcher));
        _observation = ObserveAsync(session, _view, _lifetime.Token);
        _status.Text = "已连接会话 " + session.Id + "；恢复会话可用“历史”查阅权威记录。";
    }

    private async Task ObserveAsync(AgentSession session, SessionView view, CancellationToken token)
    {
        try { await session.ObserveAsync((item, _) => { view.Apply(item); _nativeTools?.HandleEvent(item); return Task.CompletedTask; }, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { view.MarkSourceFailure("event_source", ErrorText(error)); }
    }

    private void Render(SessionViewSnapshot snapshot)
    {
        _conversation.Text = SessionViewTextFormatter.Format(snapshot); _conversation.ScrollToEnd();
        var selected = (_requests.SelectedItem as RequestItem)?.Request.Id;
        var keys = snapshot.PendingRequests.Select(x => x.Id).ToArray();
        if (!_requests.Items.Cast<RequestItem>().Select(x => x.Request.Id).SequenceEqual(keys))
        {
            _requests.ItemsSource = snapshot.PendingRequests.Select(x => new RequestItem(x)).ToArray();
            _requests.SelectedItem = _requests.Items.Cast<RequestItem>().FirstOrDefault(x => x.Request.Id == selected) ?? _requests.Items.Cast<RequestItem>().FirstOrDefault();
        }
    }

    private async Task SendAsync()
    {
        var text = _draft.Text; if (string.IsNullOrWhiteSpace(text)) return;
        await _session!.SendAsync(text); _view!.AppendUserMessage(text); _draft.Clear();
        _status.Text = "输入已接纳；等待运行、持久化及收尾事实。";
    }

    private async Task SendImageAsync()
    {
        var picker = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif" };
        if (picker.ShowDialog(this) != true) return;
        var info = new FileInfo(picker.FileName); if (info.Length > 8 * 1024 * 1024) throw new InvalidOperationException("image_too_large");
        var extension = info.Extension.ToLowerInvariant();
        var mime = extension == ".png" ? "image/png" : extension == ".webp" ? "image/webp" : extension == ".gif" ? "image/gif" : "image/jpeg";
        var blocks = new List<MessageBlock>(); var text = _draft.Text;
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(MessageBlock.Text(text));
        blocks.Add(MessageBlock.Image(mime, Convert.ToBase64String(await BoundedFiles.ReadAsync(picker.FileName, 8 * 1024 * 1024))));
        await _session!.SendBlocksAsync(blocks); _view!.AppendUserMessage(text + "\n[image: " + info.Name + "]"); _draft.Clear();
    }

    private void RenderRequest()
    {
        _questions.Children.Clear(); _questionEditors.Clear();
        if (_requests.SelectedItem is not RequestItem item) { _requestSummary.Text = "暂无待处理请求。"; return; }
        var data = item.Request.Data;
        if (item.Request.Kind == "permission")
        {
            _requestSummary.Text = Get(data, "name") + "\n" + Get(data, "summary") + "\n" +
                (data.TryGetProperty("attribution", out var attribution) ? Get(attribution, "reason") : "");
            return;
        }
        _requestSummary.Text = "回答不会代替工具授权。";
        if (!data.TryGetProperty("questions", out var questions)) return;
        foreach (var question in questions.EnumerateArray())
        {
            var editor = new QuestionEditor(question); _questionEditors.Add(editor); _questions.Children.Add(editor.Panel);
        }
    }

    private async Task ApproveAsync(bool allow)
    {
        if (_requests.SelectedItem is not RequestItem item || item.Request.Kind != "permission") throw new InvalidOperationException("select_permission_request");
        await _session!.PermissionAsync(item.Request.Id, Get(item.Request.Data, "digest"), allow);
        _status.Text = "审批回执已受理，等待服务端关闭该请求。";
    }

    private async Task AnswerAsync()
    {
        if (_requests.SelectedItem is not RequestItem item || item.Request.Kind != "question") throw new InvalidOperationException("select_question_request");
        await _session!.AnswerAsync(item.Request.Id, _questionEditors.Select(x => x.Answer()).ToArray());
        _status.Text = "答案已提交，等待服务端关闭该请求。";
    }

    private Task ShowAsync(string title, Task<JsonElement> value) => ShowResultAsync(title, value);
    private async Task ShowResultAsync(string title, Task<JsonElement> value)
    {
        var result = await value;
        new Window { Owner = this, Title = title, Width = 850, Height = 560, Content = new TextBox { Text = result.GetRawText(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
    }

    private async Task ManageCheckpointsAsync()
    {
        await ShowAsync("快照列表", _session!.ListCheckpointsAsync());
        var dialog = new Window { Owner = this, Title = "快照操作", Width = 560, Height = 210, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) }; dialog.Content = panel;
        var id = new TextBox(); panel.Children.Add(Label("输入列表中的快照 ID；恢复会改变会话上下文。")); panel.Children.Add(id);
        Button Command(string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(8) };
            button.Click += async (_, _) => { dialog.Close(); await RunAsync(action); }; return button;
        }
        panel.Children.Add(Row(Command("恢复", () => ShowAsync("恢复回执", _session.RestoreCheckpointAsync(id.Text))),
            Command("导出", async () => { var save = new SaveFileDialog { Filter = "快照|*.json" }; if (save.ShowDialog(this) == true) await File.WriteAllBytesAsync(save.FileName, await _session.ExportCheckpointAsync(id.Text)); }),
            Command("导入", async () => { var open = new OpenFileDialog { Filter = "快照|*.json" }; if (open.ShowDialog(this) == true) await ShowAsync("导入回执", _session.ImportCheckpointAsync(await BoundedFiles.ReadAsync(open.FileName, 8 * 1024 * 1024))); })));
        dialog.Show();
    }

    private async Task CloseConnectionAsync()
    {
        if (_session == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _session.CloseAsync(timeout.Token); }
        catch (Exception error) { _closeFailure = ErrorText(error); throw; }
        if (_nativeTools != null) await _nativeTools.DrainAsync(timeout.Token);
        _lifetime!.Cancel();
        if (_observation != null) await _observation;
        _nativeTools?.Dispose(); _nativeTools = null; _subscription?.Dispose(); _view?.Dispose(); _lifetime.Dispose(); _client!.Dispose();
        _session = null; _client = null; _view = null; _status.Text = "会话关闭请求已确认；未删除历史。";
    }

    private async Task DetachAsync()
    {
        // 不调用 interrupt/close/delete：明确释放本机观察和宿主资源，远端状态仍须以后查询。
        _lifetime?.Cancel();
        if (_observation != null) await _observation;
        string? cleanupFailure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { if (_nativeTools != null) await _nativeTools.DrainAsync(timeout.Token); }
        catch (Exception error) { cleanupFailure = ErrorText(error); }
        _nativeTools?.Dispose(); _nativeTools = null; _subscription?.Dispose(); _view?.Dispose();
        _lifetime?.Dispose(); _client?.Dispose(); _session = null; _client = null; _view = null;
        _status.Text = "已断开本机连接，可正常退出；未关闭或删除远端会话，远端工作可能继续。" +
            (_closeFailure == null ? "" : " 上次远端关闭未确认：" + _closeFailure) +
            (cleanupFailure == null ? "" : " 本机工具收尾未确认：" + cleanupFailure);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return;
        if (_busy) { _status.Text = "当前请求尚未返回；完成后再关闭以保全会话状态。"; return; }
        _closing = true;
        try { await CloseConnectionAsync(); _closed = true; Close(); }
        catch (Exception error) { _status.Text = "关闭未确认，可重试或选择“仅断开本机连接”后退出：" + ErrorText(error); }
        finally { _closing = false; }
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static string ErrorText(Exception error) => error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name;

    private sealed class RequestItem(SessionRequestView request)
    { public SessionRequestView Request { get; } = request; public string Label => Request.Kind + ": " + Request.Id; }

    private sealed class QuestionEditor
    {
        private readonly string _id;
        private readonly bool _multiple;
        private readonly TextBox _free = new() { MinHeight = 32, TextWrapping = TextWrapping.Wrap };
        private readonly List<(string Id, CheckBox Check)> _options = new();
        internal StackPanel Panel { get; } = new() { Margin = new Thickness(0, 8, 0, 8) };
        internal QuestionEditor(JsonElement question)
        {
            _id = Get(question, "id"); _multiple = question.TryGetProperty("allowMultiple", out var multiple) && multiple.ValueKind == JsonValueKind.True;
            Panel.Children.Add(new TextBlock { Text = Get(question, "prompt"), TextWrapping = TextWrapping.Wrap });
            foreach (var option in question.GetProperty("options").EnumerateArray())
            {
                var check = new CheckBox { Content = Get(option, "label"), Margin = new Thickness(2) };
                _options.Add((Get(option, "id"), check)); Panel.Children.Add(check);
                check.Checked += (_, _) => { if (!_multiple) foreach (var other in _options) if (!ReferenceEquals(other.Check, check)) other.Check.IsChecked = false; };
            }
            Panel.Children.Add(Label("补充文字（可空）")); Panel.Children.Add(_free);
        }
        internal QuestionAnswer Answer() => new(_id, _options.Where(x => x.Check.IsChecked == true).Select(x => x.Id).ToArray(), Empty(_free.Text));
    }
}
