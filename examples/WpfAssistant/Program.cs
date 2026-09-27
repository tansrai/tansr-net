using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
    private readonly ComboBox _narratorMode = new() { Width = 90, ItemsSource = new[] { "quiet", "normal", "verbose" }, SelectedIndex = 1 };
    private readonly TextBox _narration = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
    private SessionNarrator? _narrator;
    private Button _connect = null!;
    private string _currentToken = "";
    private AgentSession? _session;
    private TansrClient? _client;
    private ExampleConnection? _connection;
    private MediaWorkspace? _media;
    private MediaWindow? _mediaWindow;
    private SessionView? _view;
    private IDisposable? _subscription;
    private CancellationTokenSource? _lifetime;
    private Task? _observation;
    private NativeToolHost? _nativeTools;
    private readonly LocalConversationState _local = LocalConversationState.ForApplication("wpf");
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private TurnInputEditor? _inputEditor;
    private ExampleSessionControls? _controls;
    private ExampleSessionWorkspace? _workspace;
    private readonly List<Window> _sessionWindows = new();
    private string? _localSaveFailure;
    private int _draftRevision;
    private NativeMemoryDeviceHost? _memoryDevice;
    private NativeTerminalDeviceHost? _terminalDevice;
    private ExampleLocalStorage? _storage;
    private readonly TextBox _terminalOutput = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
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
            Button("历史", ShowHistoryAsync),
            Button("状态", ShowMetadataAsync),
            Button("压缩", () => ShowAsync("压缩回执", _session!.CompactAsync())),
            Button("创建快照", () => ShowAsync("快照回执", _session!.CheckpointAsync())),
            Button("媒体 / 转写 / 朗读", OpenMediaAsync), Button("快照管理", ManageCheckpointsAsync), Button("关闭会话", CloseConnectionAsync), Button("仅断开本机连接", DetachAsync)));
        top.Children.Add(Row(Button("配置 / 记忆（preview）", OpenControlsAsync), Button("提示词来源", ShowApplicationPromptAsync)));
        top.Children.Add(Row(Button("会话工作台 / 能力 / Task", OpenWorkspaceAsync), Button("撤销本机 Skills / MCP", RevokeExtensionsAsync), Label("叙述"), _narratorMode, Button("运行叙述", ShowNarrationAsync)));
        top.Children.Add(Row(Button("启动设备记忆宿主", StartMemoryDeviceAsync, false), Button("设备记忆状态", MemoryDeviceStatusAsync, false), Button("停止设备（不保证远端排空）", StopMemoryDeviceAsync, false)));
        top.Children.Add(Row(Button("连接本机设备工具", StartTerminalDeviceAsync), Button("本机工具实时输出", ShowTerminalOutputAsync), Button("停止本机设备工具", StopTerminalDeviceAsync, false)));
        top.Children.Add(new TextBlock
        {
            Text = "会话、同轮输入、审批/提问、上下文、快照及媒体使用同一公开 SDK。配置/记忆采用显式 preview；设备工具、Skills、MCP 和子代理由可信宿主装配授权。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 7),
        });
        top.Children.Add(Row(Button("本机离线回看", ShowLocalAsync, false), Button("恢复本机草稿", RestoreLocalDraftAsync, false),
            Button("保存本机草稿", () => { SaveLocal(); _status.Text = "草稿和呈现已保存在本机用户目录（明文，不含票据）。"; return Task.CompletedTask; }, false)));
        top.Children.Add(Row(Button("离线授权档案", () => ReadOfflineStorageAsync(false), false), Button("离线授权记忆", () => ReadOfflineStorageAsync(true), false)));
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        bottom.Children.Add(_draft);
        bottom.Children.Add(Row(Button("发送", SendAsync), Button("发送图片与草稿", SendImageAsync),
            Button("取消当前工作", async () => { await _session!.CancelAsync(); _status.Text = "取消请求已受理，等待运行终态。"; }),
            new TextBlock { Text = "失败或结果未知时保留草稿；请先查历史，避免重复发送。", VerticalAlignment = VerticalAlignment.Center }));
        bottom.Children.Add(Row(Button("同轮插入草稿", InsertAsync), Button("查原插入回执", QueryInputAsync), Button("显式重投原插入", RetryInputAsync)));
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
        _textMode.SelectionChanged += (_, _) => UpdateDelivery();
        _thinkingMode.SelectionChanged += (_, _) => UpdateDelivery();
        _draft.TextChanged += (_, _) => { _draftRevision++; _saveTimer.Start(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); try { SaveLocal(); } catch (Exception error) { _status.Text = "本机保存失败，草稿仍在编辑器：" + ErrorText(error); } };
        try
        {
            var saved = _local.Load();
            if (saved.Endpoint.Length > 0) { _endpoint.Text = saved.Endpoint; _resume.Text = saved.SessionId; }
            _draft.Text = saved.Draft; _conversation.Text = saved.Presentation;
            _status.Text = "离线草稿已载入；本机展示不代表当前服务端授权。票据仍需重新输入。";
        }
        catch (Exception error) { _status.Text = "本机草稿未载入：" + ErrorText(error); }
        if (Environment.GetEnvironmentVariable("TANSR_SERVE_URL") is { Length: > 0 } endpoint) _endpoint.Text = endpoint;
        if (Environment.GetEnvironmentVariable("TANSR_SESSION_TOKEN") is { Length: > 0 } token) _token.Password = token;
        _loopback.IsChecked = Environment.GetEnvironmentVariable("TANSR_ALLOW_HTTP_LOOPBACK") == "1";
        foreach (var entry in new (DependencyObject Control, string Id)[] { (_endpoint, "ServeEndpoint"), (_token, "SessionToken"), (_resume, "ResumeSession"), (_model, "SessionModel"), (_loopback, "AllowLoopback"), (_draft, "MessageDraft"), (_conversation, "Conversation"), (_status, "ConnectionStatus"), (_requests, "PendingRequests") })
            AutomationProperties.SetAutomationId(entry.Control, entry.Id);
        AutomationProperties.SetAutomationId(this, "TansrAssistant");
        AutomationProperties.SetAutomationId(_textMode, "TextDeliveryMode");
        AutomationProperties.SetAutomationId(_thinkingMode, "ThinkingDeliveryMode");
        AutomationProperties.SetAutomationId(_narratorMode, "NarratorVerbosity");
        AutomationProperties.SetAutomationId(_narration, "SessionNarration");
        Closing += OnClosing;
        SetEnabled();
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
    private static WrapPanel Row(params UIElement[] elements)
    { var panel = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) }; foreach (var element in elements) panel.Children.Add(element); return panel; }

    private Button Button(string text, Func<Task> action, bool needsSession = true)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3) };
        AutomationProperties.SetAutomationId(button, text);
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
        _textMode.IsEnabled = _thinkingMode.IsEnabled = !_closing;
        _narratorMode.IsEnabled = _session == null;
        _endpoint.IsEnabled = _resume.IsEnabled = _session == null && !_busy;
    }

    private async Task ConnectAsync()
    {
        SaveLocal();
        Volatile.Write(ref _currentToken, _token.Password);
        var connection = await ExampleConnection.ConnectAsync(_endpoint.Text, _ => Task.FromResult(Volatile.Read(ref _currentToken)), _loopback.IsChecked == true);
        var client = connection.Client;
        AgentSession session;
        CreateSessionOptions createOptions;
        try { createOptions = ExampleSessionOptions.Create(_model.Text, _resume.Text, NativeToolHost.GetDeclarations(connection.NativeTools)); session = await client.CreateSessionAsync(createOptions); }
        catch { await connection.CloseAsync(); throw; }
        _connection = connection; _client = client; _session = session; _media = new MediaWorkspace(session); _resume.Text = session.Id; _closeFailure = null;
        var savedInput = _local.Snapshot.Endpoint == _endpoint.Text ? _local.Snapshot.Input : null;
        _inputEditor = new TurnInputEditor(session, savedInput, _local.SaveInput);
        _controls = connection.SessionControl == null ? null : new ExampleSessionControls(connection.SessionControl, _endpoint.Text, session.Id, _local.PathName, connection.Profile, connection.Contract);
        _lifetime = new CancellationTokenSource();
        _nativeTools = new NativeToolHost(session, "Tansr.WPF", async (title, ct) =>
        {
            await Dispatcher.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); Title = title; }, DispatcherPriority.Normal, ct);
        }, code => Dispatcher.BeginInvoke(new Action(() => _status.Text = code)), connection.NativeTools, resumeRequested: createOptions.ResumeSessionId != null);
        try { await _nativeTools.Ready; }
        catch { await DetachAsync(); throw; }
        _view = new SessionView(new SessionViewOptions
        {
            TextDelivery = _textMode.SelectedIndex == 1 ? TextDeliveryMode.Final : TextDeliveryMode.Stream,
            ThinkingDelivery = (ThinkingDeliveryMode)_thinkingMode.SelectedIndex,
        });
        _subscription = _view.Subscribe(Render, new DispatcherSynchronizationContext(Dispatcher));
        _narration.Clear();
        _narrator = new SessionNarrator(line => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            if (_narration.Text.Length > 65536) _narration.Text = _narration.Text.Substring(_narration.Text.Length - 32768);
            _narration.AppendText(line + Environment.NewLine); _narration.ScrollToEnd();
        })), new SessionNarratorOptions { Verbosity = (NarratorVerbosity)_narratorMode.SelectedIndex });
        _storage = new ExampleLocalStorage(client, session, connection.Endpoint);
        _workspace = new ExampleSessionWorkspace(client, session, () => _view.Snapshot, connection.DescribeServices, _lifetime.Token, _storage.ExecuteAsync);
        _observation = ObserveAsync(session, _view, _lifetime.Token);
        _status.Text = "已连接会话 " + session.Id + "；恢复会话可用“历史”查阅权威记录。";
        TrySaveForExit();
    }

    private void UpdateDelivery()
    {
        _view?.SetDelivery(_textMode.SelectedIndex == 1 ? TextDeliveryMode.Final : TextDeliveryMode.Stream, (ThinkingDeliveryMode)_thinkingMode.SelectedIndex);
    }

    private async Task ObserveAsync(AgentSession session, SessionView view, CancellationToken token)
    {
        var storage = _storage; var narrator = _narrator;
        try
        {
            await session.ObserveAsync(async (item, ct) =>
        {
            view.Apply(item); narrator?.Apply(item); _nativeTools?.HandleEvent(item);
            try { if (storage != null) await storage.ObserveAsync(item, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_session, session)) _status.Text = "本地镜像保存未确认：" + ErrorText(error); }); }
        }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { view.MarkSourceFailure("event_source", ErrorText(error)); }
    }

    private void Render(SessionViewSnapshot snapshot)
    {
        _media?.Register(snapshot);
        _conversation.Text = SessionViewTextFormatter.Format(snapshot); _conversation.ScrollToEnd();
        _saveTimer.Start();
        var selected = (_requests.SelectedItem as RequestItem)?.Request.Id;
        var keys = snapshot.PendingRequests.Select(x => x.Id).ToArray();
        if (!_requests.Items.Cast<RequestItem>().Select(x => x.Request.Id).SequenceEqual(keys))
        {
            _requests.ItemsSource = snapshot.PendingRequests.Select(x => new RequestItem(x)).ToArray();
            _requests.SelectedItem = _requests.Items.Cast<RequestItem>().FirstOrDefault(x => x.Request.Id == selected) ?? _requests.Items.Cast<RequestItem>().FirstOrDefault();
        }
    }

    private Task OpenMediaAsync()
    {
        if (_mediaWindow != null) { _mediaWindow.Activate(); return Task.CompletedTask; }
        var session = _session;
        _mediaWindow = new MediaWindow(_media!, text => { if (ReferenceEquals(_session, session)) _draft.Text += (_draft.Text.Length == 0 ? "" : Environment.NewLine) + text; }, _draft.Text) { Owner = this };
        _mediaWindow.Closed += (_, _) => _mediaWindow = null; _mediaWindow.Show(); return Task.CompletedTask;
    }

    private Task ShowNarrationAsync()
    {
        if (_narration.Parent != null) return Task.CompletedTask;
        var window = new Window { Title = "运行叙述（不包含思考正文）", Width = 850, Height = 540, Content = _narration };
        AutomationProperties.SetAutomationId(window, "SessionNarrator");
        window.Closed += (_, _) => window.Content = null; ShowSessionWindow(window); return Task.CompletedTask;
    }

    private async Task SendAsync()
    {
        var text = _draft.Text; var revision = _draftRevision; if (string.IsNullOrWhiteSpace(text)) return;
        SaveLocal(); await _session!.SendAsync(text); _view!.AppendUserMessage(text); if (_draftRevision == revision) _draft.Clear(); SaveLocal();
        _status.Text = "输入已接纳；等待运行、持久化及收尾事实。";
    }

    private async Task SendImageAsync()
    {
        var picker = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif" };
        if (picker.ShowDialog(this) != true) return;
        var info = new FileInfo(picker.FileName); if (info.Length > 8 * 1024 * 1024) throw new InvalidOperationException("image_too_large");
        var extension = info.Extension.ToLowerInvariant();
        var mime = extension == ".png" ? "image/png" : extension == ".webp" ? "image/webp" : extension == ".gif" ? "image/gif" : "image/jpeg";
        var blocks = new List<MessageBlock>(); var text = _draft.Text; var revision = _draftRevision;
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(MessageBlock.Text(text));
        blocks.Add(MessageBlock.Image(mime, Convert.ToBase64String(await BoundedFiles.ReadAsync(picker.FileName, 8 * 1024 * 1024))));
        SaveLocal(); await _session!.SendBlocksAsync(blocks); _view!.AppendUserMessage(text + "\n[image: " + info.Name + "]"); if (_draftRevision == revision) _draft.Clear(); SaveLocal();
    }

    private void SaveLocal() => _local.Save(_endpoint.Text, _session?.Id ?? _resume.Text, _draft.Text, _conversation.Text);
    private void TrySaveForExit() { try { SaveLocal(); _localSaveFailure = null; } catch (Exception error) { _localSaveFailure = "本机保存失败：" + ErrorText(error); _status.Text = _localSaveFailure; } }
    private async Task ShowHistoryAsync()
    {
        var history = await _session!.GetHistoryAsync();
        _local.Save(_endpoint.Text, _session.Id, _draft.Text, _conversation.Text, history.GetRawText());
        await ShowAsync("服务端历史（已保留本机展示副本）", Task.FromResult(history));
    }
    private Task ShowLocalAsync()
    {
        var saved = _local.Snapshot;
        new Window
        {
            Owner = this,
            Title = "本机离线呈现 · " + saved.SavedAt,
            Width = 850,
            Height = 560,
            Content = new TextBox { Text = saved.OfflineText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        }.Show();
        return Task.CompletedTask;
    }
    private Task RestoreLocalDraftAsync() { _draft.Text = _local.Snapshot.Draft; _status.Text = "已恢复本机草稿；尚未发送。"; return Task.CompletedTask; }
    private async Task ReadOfflineStorageAsync(bool memory)
    {
        var picker = new OpenFileDialog { Title = "选择离线存储受信配置（只读、不连接 Serve）", Filter = "受信配置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        var value = memory ? await NativeOfflineStorageReader.ReadMemoryAsync(picker.FileName) : await NativeOfflineStorageReader.ReadArchiveAsync(picker.FileName);
        var output = new TextBox { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(output, "OfflineStorageResult");
        var window = new Window { Owner = this, Title = "离线授权存储（已知本地授权，不代表实时服务端状态）", Width = 850, Height = 600, Content = output };
        AutomationProperties.SetAutomationId(window, "OfflineStorage"); window.Show();
    }
    private async Task InsertAsync()
    {
        var text = _draft.Text; var revision = _draftRevision; SaveLocal();
        _status.Text = await _inputEditor!.InsertAsync(text);
        if (_inputEditor.Current?.Outcome == "accepted" && _draftRevision == revision) _draft.Clear();
        SaveLocal();
    }
    private async Task QueryInputAsync() => _status.Text = await _inputEditor!.QueryAsync();
    private async Task RetryInputAsync() => _status.Text = await _inputEditor!.RetryOriginalAsync();
    private Task OpenControlsAsync() { ShowSessionWindow(new SessionControlsWindow(_controls, _lifetime!.Token)); return Task.CompletedTask; }
    private Task OpenWorkspaceAsync() { ShowSessionWindow(new WorkspaceWindow(_workspace!, _lifetime!.Token)); return Task.CompletedTask; }
    private async Task RevokeExtensionsAsync() { await _connection!.RevokeExtensionsAsync(); _status.Text = "本机 Skills/MCP 已撤销；已声明工具后续调用将拒绝。重新装配需要新连接。"; }
    private void ShowSessionWindow(Window window)
    { window.Owner = this; _sessionWindows.Add(window); window.Closed += (_, _) => _sessionWindows.Remove(window); window.Show(); }
    private void CloseSessionWindows() { foreach (var window in _sessionWindows.ToArray()) window.Close(); _workspace = null; }
    private async Task StartMemoryDeviceAsync()
    {
        if (_memoryDevice != null) throw new InvalidOperationException("memory_device_already_started");
        var picker = new OpenFileDialog { Title = "选择宿主受信设备记忆配置", Filter = "受信配置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        if (_terminalDevice != null && (await NativeMemoryDeviceConfiguration.LoadAsync(picker.FileName)).SessionId == _session?.Id)
            throw new InvalidOperationException("同一会话请在本机设备配置中加入 publication；文件、Shell 和自动记忆共用同一绑定。请先停止旧设备，再重新装配统一配置。");
        _memoryDevice = await NativeMemoryDeviceHost.StartAsync(picker.FileName);
        _ = ObserveMemoryDeviceAsync(_memoryDevice);
        _status.Text = await _memoryDevice.ReadStatusAsync();
    }
    private async Task ObserveMemoryDeviceAsync(NativeMemoryDeviceHost host)
    {
        try { await host.Completion; }
        catch (Exception error) { if (ReferenceEquals(_memoryDevice, host)) _status.Text = "设备记忆领取失败；保留原库对账：" + ErrorText(error); }
    }
    private async Task MemoryDeviceStatusAsync() => _status.Text = _memoryDevice == null ? "设备记忆宿主尚未启动；需受信Serve装配及配置。" : await _memoryDevice.ReadStatusAsync();
    private async Task StopMemoryDeviceAsync()
    {
        var host = _memoryDevice; if (host == null) return;
        try { await host.StopAsync(); _status.Text = "本机设备与存储已收尾；没有关闭远端会话，也不证明远端记忆已排空。"; }
        finally { _memoryDevice = null; }
    }

    private async Task StartTerminalDeviceAsync()
    {
        if (_terminalDevice != null) throw new InvalidOperationException("terminal_device_already_started");
        if (_memoryDevice?.SessionId == _session?.Id) throw new InvalidOperationException("请先停止独立记忆设备，再使用含 publication 的统一设备配置，避免替换原会话绑定。");
        var picker = new OpenFileDialog { Title = "选择本机会话受信设备配置", Filter = "受信配置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        var session = _session!;
        _terminalDevice = await NativeTerminalDeviceHost.StartAsync(picker.FileName, session.Id, _connection!.Endpoint,
            ApproveDeviceAsync, text => Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(_session, session)) AppendTerminalOutput(text); })), _lifetime!.Token, _connection.Client, _connection.Contract, _nativeTools);
        _status.Text = _terminalDevice.Status; _ = WatchTerminalDeviceAsync(_terminalDevice);
    }
    private Task<bool> ApproveDeviceAsync(string description, CancellationToken ct)
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ct.IsCancellationRequested) { response.TrySetCanceled(); return; }
            var dialog = new Window { Title = "本机设备操作批准", Width = 680, Height = 430 };
            AutomationProperties.SetAutomationId(dialog, "DeviceApproval");
            var panel = new DockPanel { Margin = new Thickness(12) }; dialog.Content = panel;
            var row = new WrapPanel(); DockPanel.SetDock(row, Dock.Bottom); panel.Children.Add(row);
            panel.Children.Add(new TextBox { Text = description, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            foreach (var allowed in new[] { true, false })
            {
                var button = new Button { Content = allowed ? "批准本机操作" : "拒绝本机操作", Margin = new Thickness(5), Padding = new Thickness(8) };
                AutomationProperties.SetAutomationId(button, allowed ? "DeviceAllow" : "DeviceDeny");
                button.Click += (_, _) => { response.TrySetResult(allowed && !ct.IsCancellationRequested); dialog.Close(); }; row.Children.Add(button);
            }
            CancellationTokenRegistration registration = default;
            dialog.Closed += (_, _) => { registration.Dispose(); response.TrySetResult(false); };
            ShowSessionWindow(dialog);
            registration = ct.Register(() => Dispatcher.BeginInvoke(new Action(() => { response.TrySetCanceled(); dialog.Close(); })));
        }));
        return response.Task;
    }
    private async Task WatchTerminalDeviceAsync(NativeTerminalDeviceHost device)
    { try { await device.Completion; } catch (Exception error) { if (ReferenceEquals(_terminalDevice, device)) _status.Text = "设备运行失败：" + ErrorText(error); } }
    private void AppendTerminalOutput(string text)
    { if (_terminalOutput.Text.Length > 65536) _terminalOutput.Text = "[较早呈现已截断，请按原操作查账]\n" + _terminalOutput.Text.Substring(_terminalOutput.Text.Length - 32768); _terminalOutput.AppendText(text + "\n"); _terminalOutput.ScrollToEnd(); }
    private Task ShowTerminalOutputAsync()
    {
        if (_terminalOutput.Parent != null) return Task.CompletedTask;
        var window = new Window { Title = "本机工具实时输出（Serve 权威流）", Width = 850, Height = 560, Content = _terminalOutput };
        AutomationProperties.SetAutomationId(_terminalOutput, "TerminalOutput");
        window.Closed += (_, _) => window.Content = null; ShowSessionWindow(window); return Task.CompletedTask;
    }
    private async Task StopTerminalDeviceAsync()
    { var device = _terminalDevice; if (device == null) return; await device.StopAsync(); _terminalDevice = null; _status.Text = "本机设备已停止；远端会话未删除。"; }

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
    private async Task ShowMetadataAsync()
    {
        var metadata = await _session!.ReadMetadataAsync(_lifetime!.Token);
        new Window { Owner = this, Title = "会话状态 / 上下文", Width = 850, Height = 640, Content = new TextBox { Text = SessionContextText.Format(metadata), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
    }
    private async Task ShowApplicationPromptAsync()
    {
        var value = await _session!.ReadApplicationPromptAsync(_lifetime!.Token);
        new Window { Owner = this, Title = "提示词来源", Width = 700, Height = 260, Content = new TextBox { Text = ApplicationPromptText.Format(value), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
    }
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
        TrySaveForExit();
        if (_session == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (_storage != null) await _storage.FlushAsync(timeout.Token);
        try
        {
            if (_terminalDevice != null)
            {
                await _terminalDevice.PrepareSessionCloseAsync(timeout.Token);
                _status.Text = "设备通知已停止；工具与记忆仍可完成核心收尾。";
            }
            await _session.CloseAsync(timeout.Token);
        }
        catch (Exception error) { _closeFailure = ErrorText(error); throw; }
        var settled = _connection?.Observation == null ? null : await _connection.Observation.WaitForResourcesAsync(_session.Id, TimeSpan.FromSeconds(10), sessionContract: _connection.Contract, cancellationToken: timeout.Token);
        if (_nativeTools != null) await _nativeTools.DrainAsync(timeout.Token);
        await StopTerminalDeviceAsync();
        _lifetime!.Cancel();
        CloseSessionWindows();
        if (_observation != null) await _observation;
        if (_storage != null) { if (_storage.PendingCleanupStatus.Length > 0) _localSaveFailure += _storage.PendingCleanupStatus; await _storage.CloseAsync(); }
        _storage = null;
        _mediaWindow?.Close(); _mediaWindow = null; _media?.Dispose(); _media = null;
        _nativeTools?.Dispose(); _nativeTools = null; _narrator?.Dispose(); _narrator = null; _subscription?.Dispose(); _view?.Dispose();
        if (_connection != null) await _connection.CloseAsync(); _connection = null;
        _lifetime?.Dispose(); _lifetime = null;
        _session = null; _client = null; _view = null; _controls = null; _status.Text = (settled?.Completed == true ? "核心资源已确认排空；未删除历史。" : "会话关闭请求已受理；旧合同未提供远端排空证明，未删除历史。") + _localSaveFailure;
    }

    private async Task DetachAsync()
    {
        TrySaveForExit();
        var ownedLocalServe = _connection?.OwnsLocalServe == true;
        using var storageTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { if (_storage != null) await _storage.FlushAsync(storageTimeout.Token); }
        catch (Exception error) { _localSaveFailure = " 本地上下文镜像未确认：" + ErrorText(error); }
        // 不调用 interrupt/close/delete：明确释放本机观察和宿主资源，远端状态仍须以后查询。
        _lifetime?.Cancel();
        CloseSessionWindows();
        if (_observation != null) await _observation;
        if (_storage != null) { if (_storage.PendingCleanupStatus.Length > 0) _localSaveFailure += _storage.PendingCleanupStatus; await _storage.CloseAsync(); }
        _storage = null;
        string? cleanupFailure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await StopTerminalDeviceAsync(); } catch (Exception error) { cleanupFailure = ErrorText(error); }
        try { if (_nativeTools != null) await _nativeTools.DrainAsync(timeout.Token); }
        catch (Exception error) { cleanupFailure = ErrorText(error); }
        _mediaWindow?.Close(); _mediaWindow = null; _media?.Dispose(); _media = null;
        _nativeTools?.Dispose(); _nativeTools = null; _narrator?.Dispose(); _narrator = null; _subscription?.Dispose(); _view?.Dispose();
        _lifetime?.Dispose(); _lifetime = null;
        try { if (_connection != null) await _connection.CloseAsync(); }
        catch (Exception error) { cleanupFailure = ErrorText(error); }
        finally { _connection = null; _session = null; _client = null; _view = null; }
        _status.Text = (ownedLocalServe ? (cleanupFailure == null ? "已断开并回收本实例启动的本地 Serve；未删除历史。" : "已断开本机连接；本地 Serve 或 MCP 收尾未确认，可退出，未删除历史。") : "已断开本机连接，可正常退出；未关闭或删除远端会话，远端工作可能继续。") +
            (_closeFailure == null ? "" : " 上次远端关闭未确认：" + _closeFailure) +
            (cleanupFailure == null ? "" : " 本机工具收尾未确认：" + cleanupFailure) + _localSaveFailure;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return;
        if (_memoryDevice != null) { _status.Text = "设备仍领取记忆操作。请先在控制端核对记忆工作，再显式停止设备后退出；窗口关闭不冒充远端排空。"; return; }
        if (_busy) { _status.Text = "当前请求尚未返回；完成后再关闭以保全会话状态。"; return; }
        _closing = true;
        try { await CloseConnectionAsync(); _saveTimer.Stop(); _closed = true; Close(); }
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
            AutomationProperties.SetAutomationId(_free, "QuestionAnswer_" + _id);
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
