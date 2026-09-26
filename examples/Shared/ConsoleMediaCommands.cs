using System.IO;
using Tansr.Sdk.Media;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Examples;

internal sealed class ConsoleMediaCommands(MediaWorkspace workspace, SessionView view, Action<string> write)
{
    private SpeechBatch? _speech;
    private string? _draft;
    internal async Task<bool> TryHandleAsync(string command, CancellationToken ct)
    {
        if (command == "/media" || command == "/media-history")
        {
            workspace.Register(view.Snapshot); if (command == "/media-history") await workspace.LoadHistoryAsync(ct);
            for (var index = 0; index < workspace.Items.Count; index++) write(index + ": " + workspace.Items[index]);
            if (workspace.PresentationTruncated) write("media_presentation_truncated"); return true;
        }
        if (command.StartsWith("/media-save ", StringComparison.Ordinal))
        {
            var parts = command.Substring(12).Split(new[] { ' ' }, 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var index) || index < 0 || index >= workspace.Items.Count) throw new MediaException("media_selection_required");
            // 无UI宿主不覆盖已有文件；路径由用户命令明确给出。
            var source = await workspace.MaterializeAsync(workspace.Items[index], ct);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(parts[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, 65536, ct); write("media_saved"); return true;
        }
        if (command.StartsWith("/asr ", StringComparison.Ordinal))
        {
            await workspace.LoadCatalogAsync(ct);
            var model = Environment.GetEnvironmentVariable("TANSR_ASR_MODEL") ?? workspace.TranscriptionModels.FirstOrDefault() ?? throw new MediaException("transcription_model_unavailable");
            var path = command.Substring(5); var mime = Path.GetExtension(path).ToLowerInvariant() switch
            { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg", ".webm" => "audio/webm", ".flac" => "audio/flac", _ => throw new MediaException("audio_format_unsupported") };
            _draft = await workspace.TranscribeAsync(await BoundedFiles.ReadAsync(path, 16 * 1024 * 1024), mime, model, ct);
            write("transcription_draft（未发送；/send-draft显式发送）\n" + _draft); return true;
        }
        if (command == "/send-draft")
        {
            if (_draft == null) throw new MediaException("transcription_draft_missing");
            await workspace.Session.SendAsync(_draft, ct); _draft = null; write("draft_accepted"); return true;
        }
        if (command.StartsWith("/speech-plan ", StringComparison.Ordinal))
        {
            if (_speech != null && _speech.States.Any(x => x != SpeechSegmentState.Ready)) throw new MediaException("speech_batch_exists_use_speech_reset_explicitly");
            await workspace.LoadCatalogAsync(ct);
            var desired = Environment.GetEnvironmentVariable("TANSR_TTS_MODEL");
            var model = workspace.SpeechModels.FirstOrDefault(x => desired == null || x.Model == desired) ?? throw new MediaException("speech_model_unavailable");
            _speech = new SpeechBatch(model.Plan(command.Substring(13), true), new SpeechOptions { Model = model.Model, Voice = model.DefaultVoice, Format = model.Formats.FirstOrDefault() });
            write("speech_segments=" + _speech.Plan.Segments.Count + " estimated_characters=" + _speech.Plan.EstimatedCharacters + "；每个 /speech-next 合成一段，可能分别计费。"); return true;
        }
        if (command == "/speech-reset") { _speech = null; write("已明确重置本地批次；重建相同文字可能重复付费，未知请求仍需对账。"); return true; }
        if (command == "/speech-next")
        {
            var batch = _speech ?? throw new MediaException("speech_batch_not_prepared"); if (batch.States.Any(x => x == SpeechSegmentState.Unknown)) throw new MediaException("speech_result_unknown_do_not_repeat");
            var index = batch.States.ToList().FindIndex(x => x == SpeechSegmentState.Ready); if (index < 0) { write("speech_batch_completed"); return true; }
            workspace.Add(await batch.SpeakSegmentAsync(index, workspace.Session.SpeakAsync, ct), "朗读段 " + (index + 1)); write("speech_segment_completed=" + (index + 1) + "；/media列出，/media-save保存。"); return true;
        }
        return false;
    }
}
