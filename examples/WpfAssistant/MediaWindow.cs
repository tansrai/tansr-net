using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tansr.Examples;
using Tansr.Sdk.Media;
using Tansr.Sdk.Sessions;

namespace WpfAssistant;

internal sealed class MediaWindow : Window
{
    private readonly MediaWorkspace _workspace;
    private readonly Action<string> _draft;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _operation;
    private readonly ListBox _items = new() { Width = 350 };
    private readonly ComboBox _asr = new() { MinWidth = 180 };
    private readonly ComboBox _model = new() { MinWidth = 180 };
    private readonly ComboBox _voice = new() { MinWidth = 100 };
    private readonly ComboBox _format = new() { MinWidth = 75 };
    private readonly CheckBox _segment = new() { Content = "允许分段（可能产生多次计费）" };
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _image = new() { Stretch = System.Windows.Media.Stretch.Uniform };
    private readonly MediaElement _player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close };
    private readonly Grid _preview = new();
    private readonly List<Button> _actions = new();
    private WindowsAudioRecorder? _recorder;
    private SpeechBatch? _batch;
    private bool _busy, _closed;
    internal MediaWindow(MediaWorkspace workspace, Action<string> draft, string text)
    {
        _workspace = workspace; _draft = draft; _text.Text = text;
        Title = "媒体 · 原生预览 / 转写 / 分段朗读"; Width = 1100; Height = 760;
        var layout = new DockPanel { Margin = new Thickness(12) }; Content = layout;
        var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Top); layout.Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = "下载主机来自 TANSR_MEDIA_HOSTS（逗号分隔）；空名单只允许内嵌材料。ASR 只回填草稿，不自动发送。系统缺少媒体解码器时可保存后在支持的播放器打开。", TextWrapping = TextWrapping.Wrap });
        controls.Children.Add(Row(Button("刷新模型目录", CatalogAsync), _asr, Button("音频文件转草稿", FileAsrAsync), Button("开始麦克风录音", StartRecordingAsync), Button("停止并转草稿", FinishRecordingAsync)));
        controls.Children.Add(Row(_model, _voice, _format, _segment)); controls.Children.Add(_text);
        controls.Children.Add(Row(Button("建立新朗读批次", PrepareAsync), Button("合成 / 续合下一段", SpeakNextAsync), Button("恢复历史媒体", async () => { await _workspace.LoadHistoryAsync(Token); Refresh(); }), Button("刷新产物", () => { Refresh(); return Task.CompletedTask; })));
        var cancel = new Button { Content = "取消当前媒体请求", Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4) };
        cancel.Click += (_, _) => _operation?.Cancel(); controls.Children.Add(cancel);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        bottom.Children.Add(Row(Button("预览选中产物", PreviewAsync), Button("保存选中产物", SaveAsync), Button("停止播放", () => { _player.Stop(); return Task.CompletedTask; })));
        bottom.Children.Add(_status);
        DockPanel.SetDock(_items, Dock.Left); layout.Children.Add(_items);
        _preview.Children.Add(_image); _preview.Children.Add(_player); layout.Children.Add(_preview);
        _model.SelectionChanged += (_, _) => SelectModel();
        _player.MediaFailed += (_, e) => _status.Text = "系统媒体解码失败：" + e.ErrorException.GetType().Name + "；可保存产物。";
        Closed += (_, _) => { _closed = true; _stop.Cancel(); _operation?.Cancel(); _recorder?.Dispose(); _recorder = null; _player.Close(); _image.Source = null; };
        Loaded += async (_, _) => await RunAsync(CatalogAsync);
        Refresh();
    }
    private CancellationToken Token => _operation?.Token ?? _stop.Token;
    private static WrapPanel Row(params UIElement[] elements) { var row = new WrapPanel(); foreach (var element in elements) { if (element is FrameworkElement f) f.Margin = new Thickness(3); row.Children.Add(element); } return row; }
    private Button Button(string label, Func<Task> action)
    { var b = new Button { Content = label, Padding = new Thickness(8, 4, 8, 4) }; b.Click += async (_, _) => await RunAsync(action); _actions.Add(b); return b; }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return; _busy = true; foreach (var b in _actions) b.IsEnabled = false;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = "请求已取消；已发出的转写/朗读结果可能未知，请勿直接重复付费请求。"; }
        catch (Exception error) { _status.Text = error is MediaException media ? media.Code : error is Tansr.Sdk.Client.TansrException sdk ? sdk.Code : error.GetType().Name; }
        finally { _operation.Dispose(); _operation = null; _busy = false; if (!_closed) { foreach (var b in _actions) b.IsEnabled = true; Refresh(); } }
    }
    private void Refresh()
    { var selected = _items.SelectedItem; _items.ItemsSource = null; _items.ItemsSource = _workspace.Items; _items.SelectedItem = selected ?? _workspace.Items.LastOrDefault(); if (_workspace.PresentationTruncated) _status.Text += " 媒体列表已达 256 项上限；历史未删除。"; }
    private async Task CatalogAsync()
    { await _workspace.LoadCatalogAsync(Token); _model.ItemsSource = _workspace.SpeechModels; _model.SelectedIndex = 0; _asr.ItemsSource = _workspace.TranscriptionModels; _asr.SelectedIndex = 0; _status.Text = "目录已读取；朗读限制采用服务端模型约束。"; }
    private void SelectModel()
    { if (_model.SelectedItem is not SpeechModel model) return; _voice.ItemsSource = model.Voices; _voice.SelectedItem = model.DefaultVoice; if (_voice.SelectedIndex < 0) _voice.SelectedIndex = 0; _format.ItemsSource = model.Formats; _format.SelectedIndex = 0; }
    private string AsrModel => _asr.SelectedItem as string ?? throw new MediaException("transcription_model_unavailable");
    private async Task FileAsrAsync()
    {
        var model = AsrModel;
        var dialog = new OpenFileDialog { Filter = "音频|*.wav;*.mp3;*.m4a;*.ogg;*.webm;*.flac" }; if (dialog.ShowDialog(this) != true) return;
        var mime = AudioMime(dialog.FileName); var bytes = await BoundedFiles.ReadAsync(dialog.FileName, 16 * 1024 * 1024);
        var draft = await _workspace.TranscribeAsync(bytes, mime, model, Token); if (!_closed) { _draft(draft); _text.Text = draft; _status.Text = "转写已加入主窗口草稿，未发送。"; }
    }
    private Task StartRecordingAsync()
    { _ = AsrModel; if (_recorder != null) throw new MediaException("recording_already_started"); _recorder = new WindowsAudioRecorder(); _status.Text = "录音中，16kHz 单声道 WAV，最多 120 秒；点击停止并转草稿。"; return Task.CompletedTask; }
    private async Task FinishRecordingAsync()
    {
        var recorder = _recorder ?? throw new MediaException("recording_not_started"); _recorder = null;
        byte[] bytes; try { bytes = await recorder.StopAsync(); } finally { recorder.Dispose(); }
        var draft = await _workspace.TranscribeAsync(bytes, "audio/wav", AsrModel, Token); if (!_closed) { _draft(draft); _text.Text = draft; _status.Text = "录音转写已加入草稿，未发送。"; }
    }
    private Task PrepareAsync()
    {
        var model = _model.SelectedItem as SpeechModel ?? throw new MediaException("speech_model_unavailable");
        if (_batch != null && _batch.States.Any(x => x != SpeechSegmentState.Ready) && MessageBox.Show(this, "这会新建付费批次；旧批次已成功/未知的片段不会在新批次去重。确认以当前文字建立新批次？", "新批次", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return Task.CompletedTask;
        _batch = new SpeechBatch(model.Plan(_text.Text, _segment.IsChecked == true), new SpeechOptions { Model = model.Model, Voice = _voice.SelectedItem as string, Format = _format.SelectedItem as string });
        _status.Text = "已规划 " + _batch.Plan.Segments.Count + " 段，估算字符 " + _batch.Plan.EstimatedCharacters + "；点击一次仅合成下一段，已完成片段用预览播放。"; return Task.CompletedTask;
    }
    private async Task SpeakNextAsync()
    {
        var batch = _batch ?? throw new MediaException("speech_batch_not_prepared");
        if (batch.States.Any(x => x == SpeechSegmentState.Unknown)) throw new MediaException("speech_result_unknown_do_not_repeat");
        var index = batch.States.ToList().FindIndex(x => x == SpeechSegmentState.Ready); if (index < 0) { _status.Text = "所有片段已完成；选择产物预览不重复调用模型。"; return; }
        var response = await batch.SpeakSegmentAsync(index, _workspace.Session.SpeakAsync, Token); _workspace.Add(response, "朗读段 " + (index + 1));
        _status.Text = "已完成片段 " + (index + 1) + "/" + batch.Plan.Segments.Count + "；可续合下一段或预览当前段。";
    }
    private MediaItem Selected => _items.SelectedItem as MediaItem ?? throw new MediaException("media_selection_required");
    private async Task PreviewAsync()
    {
        var item = Selected; var path = await _workspace.MaterializeAsync(item, Token); _player.Close(); _image.Source = null;
        if (item.Resource?.Kind == MediaKind.Image)
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1600; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); _image.Source = bitmap; _player.Visibility = Visibility.Collapsed;
        }
        else if (item.Resource?.Kind == MediaKind.Transcript) { _text.Text = await File.ReadAllTextAsync(path, Token); _player.Visibility = Visibility.Collapsed; }
        else { _player.Visibility = Visibility.Visible; _player.Source = new Uri(path); _player.Play(); }
        _status.Text = item.Status;
    }
    private async Task SaveAsync()
    {
        var item = Selected; var path = await _workspace.MaterializeAsync(item, Token); var extension = Path.GetExtension(path);
        var dialog = new SaveFileDialog { FileName = "tansr-media" + extension, Filter = "媒体文件|*" + extension }; if (dialog.ShowDialog(this) == true) { await _workspace.SaveAsync(item, dialog.FileName, Token); _status.Text = "已保存到用户选择的位置。"; }
    }
    private static string AudioMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg", ".webm" => "audio/webm", ".flac" => "audio/flac", _ => throw new MediaException("audio_format_unsupported") };
}
