using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Tansr.Examples;
using Tansr.Sdk.Media;
using Tansr.Sdk.Sessions;

namespace WinFormsAssistant;

internal sealed class MediaForm : Form
{
    private readonly MediaWorkspace _workspace;
    private readonly Action<string> _draft;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _operation;
    private readonly ListBox _items = new() { Dock = DockStyle.Left, Width = 350, HorizontalScrollbar = true };
    private readonly ComboBox _asr = Choice(180), _model = Choice(180), _voice = Choice(130), _format = Choice(80);
    private readonly ComboBox _recordingDevice = Choice(340);
    private readonly CheckBox _segment = new() { Text = "允许分段（多次计费）", AutoSize = true };
    private readonly TextBox _text = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 90, Width = 980 };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 52 };
    private readonly ElementHost _video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    private readonly List<Button> _actions = new();
    private Action? _closePlayer;
    private WindowsAudioRecorder? _recorder;
    private bool _busy, _closed;
    internal MediaForm(MediaWorkspace workspace, Action<string> draft, string text)
    {
        _workspace = workspace; _draft = draft; _text.Text = text; Text = "媒体 · 原生预览 / 转写 / 分段朗读"; Width = 1100; Height = 760; Padding = new Padding(10);
        Name = "MediaWindow"; _items.Name = "MediaItems"; _text.Name = "MediaText"; _status.Name = "MediaStatus";
        _image.Name = "MediaImage"; _video.Name = "MediaPlayer";
        _model.Name = "MediaSpeechModel"; _asr.Name = "MediaTranscriptionModel"; _segment.Name = "MediaAllowSegmentation";
        _recordingDevice.Name = "MediaRecordingDevice"; _recordingDevice.AccessibleName = "录音输入设备";
        if (workspace.Speech != null) _text.Text = workspace.Speech.Plan.Text;
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        top.Controls.Add(new Label { Text = "媒体下载主机来自 TANSR_MEDIA_HOSTS；空名单只允许内嵌材料。ASR 只回填草稿。播放使用 Windows 媒体组件，缺少解码器时仍可保存。", AutoSize = true, MaximumSize = new Size(1000, 0) });
        top.Controls.Add(Row(Button("刷新模型目录", CatalogAsync), _asr, Button("音频文件转草稿", FileAsrAsync)));
        top.Controls.Add(Row(new Label { Text = "录音输入（先选择）", AutoSize = true }, _recordingDevice, Button("刷新录音设备", RecordingDevicesAsync), Button("开始录音", StartRecordingAsync), Button("停止并转草稿", FinishRecordingAsync)));
        top.Controls.Add(Row(_model, _voice, _format, _segment)); top.Controls.Add(_text);
        top.Controls.Add(Row(Button("建立新朗读批次", PrepareAsync), Button("合成 / 续合下一段", SpeakNextAsync), Button("恢复历史媒体", async () => { await _workspace.LoadHistoryAsync(Token); RefreshItems(); }), Button("刷新产物", () => { RefreshItems(); return Task.CompletedTask; })));
        var cancel = new Button { Name = "MediaCancel", Text = "取消当前媒体请求", AutoSize = true };
        cancel.Click += (_, _) =>
        {
            _operation?.Cancel();
            if (_recorder != null) { _recorder.Dispose(); _recorder = null; _recordingDevice.Enabled = true; _status.Text = "录音已取消并丢弃，未上传或转写。"; }
        };
        top.Controls.Add(cancel);
        var bottom = Row(Button("预览选中产物", PreviewAsync), Button("保存选中产物", SaveAsync), Button("停止播放", () => { StopPlayer(); return Task.CompletedTask; })); bottom.Dock = DockStyle.Bottom;
        Controls.Add(_video); Controls.Add(_image); Controls.Add(_items); Controls.Add(bottom); Controls.Add(_status); Controls.Add(top);
        _model.SelectedIndexChanged += (_, _) => SelectModel();
        FormClosed += (_, _) => { _closed = true; _stop.Cancel(); _operation?.Cancel(); _recorder?.Dispose(); _recorder = null; StopPlayer(); var image = _image.Image; _image.Image = null; image?.Dispose(); };
        Shown += async (_, _) => await RunAsync(async () => { await RecordingDevicesAsync(); await CatalogAsync(); }); RefreshItems();
    }
    private static ComboBox Choice(int width) => new() { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
    private CancellationToken Token => _operation?.Token ?? _stop.Token;
    private static FlowLayoutPanel Row(params Control[] controls) { var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.AddRange(controls); return row; }
    private Button Button(string label, Func<Task> action) { var b = new Button { Name = "MediaAction:" + label, Text = label, AutoSize = true }; b.Click += async (_, _) => await RunAsync(action); _actions.Add(b); return b; }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return; _busy = true; foreach (var b in _actions) b.Enabled = false; _operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "已取消；发出的付费请求可能结果未知，不自动重发。"; }
        catch (Exception error) { if (!_closed) _status.Text = error is MediaException media ? media.Code : error is Tansr.Sdk.Client.TansrException sdk ? sdk.Code : error.GetType().Name; }
        finally { _operation.Dispose(); _operation = null; _busy = false; if (!_closed) { foreach (var b in _actions) b.Enabled = true; RefreshItems(); } }
    }
    private void RefreshItems()
    { var selected = _items.SelectedItem; _items.BeginUpdate(); _items.Items.Clear(); foreach (var item in _workspace.Items) _items.Items.Add(item); _items.SelectedItem = selected ?? _workspace.Items.LastOrDefault(); _items.EndUpdate(); if (_workspace.PresentationTruncated) _status.Text += " 媒体列表达 256 项上限，历史未删除。"; }
    private async Task CatalogAsync()
    {
        await _workspace.LoadCatalogAsync(Token); _model.Items.Clear(); _model.Items.AddRange(_workspace.SpeechModels.Cast<object>().ToArray()); if (_model.Items.Count > 0) _model.SelectedIndex = 0;
        _asr.Items.Clear(); _asr.Items.AddRange(_workspace.TranscriptionModels.Cast<object>().ToArray()); if (_asr.Items.Count > 0) _asr.SelectedIndex = 0; _status.Text = "已读取服务端模型目录和实际朗读输入约束。" + _workspace.SpeechStatus;
    }
    private void SelectModel()
    {
        if (_model.SelectedItem is not SpeechModel model) return;
        _voice.Items.Clear(); _voice.Items.AddRange(model.Voices); _voice.SelectedItem = model.DefaultVoice; if (_voice.SelectedIndex < 0 && _voice.Items.Count > 0) _voice.SelectedIndex = 0;
        _format.Items.Clear(); _format.Items.AddRange(model.Formats); if (_format.Items.Count > 0) _format.SelectedIndex = 0;
    }
    private string AsrModel => _asr.SelectedItem as string ?? throw new MediaException("transcription_model_unavailable");
    private async Task FileAsrAsync()
    {
        var model = AsrModel; using var dialog = new OpenFileDialog { Filter = "音频|*.wav;*.mp3;*.m4a;*.ogg;*.webm;*.flac" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var bytes = await BoundedFiles.ReadAsync(dialog.FileName, 16 * 1024 * 1024); var draft = await _workspace.TranscribeAsync(bytes, AudioMime(dialog.FileName), model, Token);
        if (!_closed) { _draft(draft); _text.Text = draft; _status.Text = "转写已回填主窗口草稿，未发送。"; }
    }
    private Task StartRecordingAsync()
    {
        _ = AsrModel; if (_recorder != null) throw new MediaException("recording_already_started");
        var input = _recordingDevice.SelectedItem as WindowsAudioRecorder.InputDevice ?? throw new MediaException("recording_device_selection_required");
        _recorder = new WindowsAudioRecorder(input); _recordingDevice.Enabled = false;
        _status.Text = input.Name + "：16kHz 单声道 WAV 录音中，最长 120 秒。"; return Task.CompletedTask;
    }
    private Task RecordingDevicesAsync()
    {
        if (_recorder != null) throw new MediaException("recording_already_started");
        _recordingDevice.Items.Clear(); _recordingDevice.Items.Add(WindowsAudioRecorder.InputDevice.SystemDefault);
        _recordingDevice.Items.AddRange(WindowsAudioRecorder.EnumerateInputDevices().Cast<object>().ToArray()); _recordingDevice.SelectedIndex = -1;
        _status.Text = "请选择录音输入；刷新和选择不会采集，也不改变系统默认设备。"; return Task.CompletedTask;
    }
    private async Task FinishRecordingAsync()
    {
        var recorder = _recorder ?? throw new MediaException("recording_not_started"); _recorder = null;
        byte[] bytes; try { bytes = await recorder.StopAsync(); } finally { recorder.Dispose(); _recordingDevice.Enabled = true; }
        var draft = await _workspace.TranscribeAsync(bytes, "audio/wav", AsrModel, Token); if (!_closed) { _draft(draft); _text.Text = draft; _status.Text = "录音转写已回填草稿，未发送。"; }
    }
    private Task PrepareAsync()
    {
        var model = _model.SelectedItem as SpeechModel ?? throw new MediaException("speech_model_unavailable");
        if (_workspace.Speech != null && _workspace.Speech.States.Any(x => x != SpeechSegmentState.Ready) && MessageBox.Show(this, "新批次可能重复计费；已成功或未知片段不会跨批次去重。确认新建？", "建立新批次", MessageBoxButtons.OKCancel) != DialogResult.OK) return Task.CompletedTask;
        var batch = _workspace.PrepareSpeech(_text.Text, model, _voice.SelectedItem as string, _format.SelectedItem as string, _segment.Checked, true);
        _status.Text = "已规划 " + batch.Plan.Segments.Count + " 段，估算字符 " + batch.Plan.EstimatedCharacters + "；每次按钮合成下一段。"; return Task.CompletedTask;
    }
    private async Task SpeakNextAsync()
    {
        var batch = _workspace.Speech ?? throw new MediaException("speech_batch_not_prepared"); if (batch.States.Any(x => x == SpeechSegmentState.Unknown)) throw new MediaException("speech_result_unknown_do_not_repeat");
        var index = batch.States.ToList().FindIndex(x => x == SpeechSegmentState.Ready); if (index < 0) { _status.Text = "全部片段已完成；预览不重复调用模型。"; return; }
        await _workspace.SpeakNextAsync(Token); _status.Text = "已完成片段 " + (index + 1) + "/" + batch.Plan.Segments.Count;
    }
    private MediaItem Selected => _items.SelectedItem as MediaItem ?? throw new MediaException("media_selection_required");
    private async Task PreviewAsync()
    {
        var item = Selected; var path = await _workspace.MaterializeAsync(item, Token); if (_closed) return; StopPlayer(); var old = _image.Image; _image.Image = null; old?.Dispose();
        if (item.Resource?.Kind == MediaKind.Image)
        {
            using var source = Image.FromFile(path); if ((long)source.Width * source.Height > 64000000) throw new MediaException("image_dimensions_too_large");
            _image.Image = new Bitmap(source); _image.Visible = true; _video.Visible = false;
        }
        else if (item.Resource?.Kind == MediaKind.Transcript) { _text.Text = File.ReadAllText(path); }
        else
        {
            _image.Visible = false; _video.Visible = true;
            // 复用 Framework 自带的 WPF 媒体管道；只读取本实例已校验的本地材料。
            await PlayAsync(path, item, Token);
        }
        _status.Text = item.Status;
    }
    private async Task PlayAsync(string path, MediaItem item, CancellationToken ct)
    {
        var player = new System.Windows.Controls.MediaElement
        {
            LoadedBehavior = System.Windows.Controls.MediaState.Manual,
            UnloadedBehavior = System.Windows.Controls.MediaState.Close,
            Stretch = System.Windows.Media.Stretch.Uniform,
        };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = true;
        MediaException? playbackFailure = null;
        System.Windows.RoutedEventHandler onOpened = (_, _) =>
        {
            if (!active || _closed) return;
            if (item.Resource?.Kind == MediaKind.Video && (player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0))
                opened.TrySetException(new MediaException("native_media_video_unavailable"));
            else opened.TrySetResult(true);
        };
        EventHandler<System.Windows.ExceptionRoutedEventArgs> onFailed = (_, _) =>
        {
            if (!active || _closed) return;
            _status.Text = "native_media_codec_or_playback_error";
            playbackFailure = new MediaException("native_media_codec_or_playback_error");
            opened.TrySetException(playbackFailure); StopPlayer();
        };
        System.Windows.RoutedEventHandler onEnded = (_, _) =>
        {
            // 保留末帧供用户查看；停止、换产物或关窗统一释放这唯一播放器。
            if (active && !_closed) { player.Pause(); _status.Text = item.Status + "；原生播放已结束。"; }
        };
        player.MediaOpened += onOpened; player.MediaFailed += onFailed; player.MediaEnded += onEnded;
        _closePlayer = () =>
        {
            active = false;
            player.MediaOpened -= onOpened; player.MediaFailed -= onFailed; player.MediaEnded -= onEnded;
            opened.TrySetCanceled(); player.Stop(); player.Close(); _video.Child = null;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = ct.Register(() => opened.TrySetCanceled());
        using var expired = timeout.Token.Register(() => opened.TrySetException(new MediaException("native_media_open_timeout")));
        try
        {
            ct.ThrowIfCancellationRequested(); _video.Child = player; player.Source = new Uri(path); player.Play();
            await opened.Task;
            ct.ThrowIfCancellationRequested();
            if (playbackFailure != null) throw playbackFailure;
        }
        catch { StopPlayer(); throw; }
    }
    private void StopPlayer() { var close = _closePlayer; _closePlayer = null; close?.Invoke(); }
    private async Task SaveAsync()
    {
        var item = Selected; var path = await _workspace.MaterializeAsync(item, Token); using var dialog = new SaveFileDialog { FileName = "tansr-media" + Path.GetExtension(path), Filter = "媒体文件|*" + Path.GetExtension(path) };
        if (dialog.ShowDialog(this) == DialogResult.OK) { await _workspace.SaveAsync(item, dialog.FileName, Token); _status.Text = "已保存到用户选择的位置。"; }
    }
    private static string AudioMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg", ".webm" => "audio/webm", ".flac" => "audio/flac", _ => throw new MediaException("audio_format_unsupported") };
}
