using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Media;

public enum SpeechInputUnit { UnicodeCodePoints, Utf16CodeUnits, Utf8Bytes }
public enum SpeechSegmentState { Ready, Running, Done, Unknown }

public sealed class SpeechPlanOptions
{
    /// <summary>必须来自当前授权模型目录的 maxChars；未知时不能猜测。</summary>
    public int? MaximumWeightedCharacters { get; set; }
    public int? MaximumInputUnits { get; set; }
    public SpeechInputUnit InputUnit { get; set; } = SpeechInputUnit.UnicodeCodePoints;
    public bool AllowSegmentation { get; set; }
}

public sealed class SpeechPlan
{
    private SpeechPlan(string text, int estimate, IList<string> segments)
    { Text = text; EstimatedCharacters = estimate; Segments = new ReadOnlyCollection<string>(segments); }
    public string Text { get; }
    public int EstimatedCharacters { get; }
    public IReadOnlyList<string> Segments { get; }

    public static SpeechPlan Create(string input, SpeechPlanOptions options)
    {
        if (input == null || options == null) throw new ArgumentNullException(input == null ? nameof(input) : nameof(options));
        if (!options.MaximumWeightedCharacters.HasValue || options.MaximumWeightedCharacters <= 0) throw new MediaException("speech_limit_unknown");
        if (options.MaximumInputUnits <= 0 || !Enum.IsDefined(typeof(SpeechInputUnit), options.InputUnit)) throw new MediaException("speech_invalid_limit");
        var text = input.Trim(); if (text.Length == 0) throw new MediaException("speech_empty_input");
        // 先限制UTF16上界，再按Unicode标量计数，避免超大草稿造成临时列表膨胀。
        if (text.Length > 64000) throw new MediaException("speech_input_too_large");
        var scalars = Scalars(text).ToList(); var total = scalars.Sum(x => Han(char.ConvertToUtf32(x, 0)) ? 2 : 1);
        if (total > 32000) throw new MediaException("speech_input_too_large");
        var segments = new List<string>(); var current = new StringBuilder(); var used = 0; var units = 0;
        foreach (var scalar in scalars)
        {
            var weight = Han(char.ConvertToUtf32(scalar, 0)) ? 2 : 1;
            var atomic = options.InputUnit == SpeechInputUnit.Utf16CodeUnits ? scalar.Length : options.InputUnit == SpeechInputUnit.Utf8Bytes ? Encoding.UTF8.GetByteCount(scalar) : 1;
            if (weight > options.MaximumWeightedCharacters.Value || atomic > options.MaximumInputUnits) throw new MediaException("speech_input_too_large");
            if (used + weight > options.MaximumWeightedCharacters.Value || current.Length + scalar.Length > 20000 || units + atomic > options.MaximumInputUnits)
            {
                if (!options.AllowSegmentation) throw new MediaException("speech_segmentation_required");
                segments.Add(current.ToString()); current.Clear(); used = 0; units = 0;
            }
            current.Append(scalar); used += weight; units += atomic;
        }
        if (current.Length > 0) segments.Add(current.ToString());
        if (segments.Count > 32) throw new MediaException("speech_segment_limit");
        if (segments.Any(string.IsNullOrWhiteSpace)) throw new MediaException("speech_empty_segment");
        return new SpeechPlan(text, total, segments);
    }
    private static IEnumerable<string> Scalars(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            { if (index + 1 == text.Length || !char.IsLowSurrogate(text[index + 1])) throw new MediaException("speech_invalid_unicode"); yield return text.Substring(index++, 2); }
            else { if (char.IsLowSurrogate(text[index])) throw new MediaException("speech_invalid_unicode"); yield return text[index].ToString(); }
        }
    }
    // 与仓内 Node v22.22.1 / Unicode 17.0 的 \p{Script=Han} 生成区间一致；只用于朗读输入估算，不代替服务计费。
    private static readonly int[] HanRanges = { 11904, 11929, 11931, 12019, 12032, 12245, 12293, 12293, 12295, 12295, 12321, 12329, 12344, 12347, 13312, 19903, 19968, 40959, 63744, 64109, 64112, 64217, 94178, 94179, 94192, 94198, 131072, 173791, 173824, 178205, 178208, 183981, 183984, 191456, 191472, 192093, 194560, 195101, 196608, 201546, 201552, 210041 };
    private static bool Han(int point) { for (var i = 0; i < HanRanges.Length; i += 2) if (point >= HanRanges[i] && point <= HanRanges[i + 1]) return true; return false; }
}

/// <summary>本地朗读批次；成功片段缓存，未知付费请求禁止自动重发。重新创建批次须由用户明确选择。</summary>
public sealed class SpeechBatch
{
    private readonly object _gate = new();
    private readonly SpeechOptions _options;
    private readonly SpeechSegmentState[] _states;
    private readonly JsonElement?[] _results;
    private readonly Task<JsonElement>?[] _tasks;
    public SpeechBatch(SpeechPlan plan, SpeechOptions options)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (options == null) throw new ArgumentNullException(nameof(options));
        _options = new SpeechOptions { Model = options.Model, Voice = options.Voice, Format = options.Format, Speed = options.Speed };
        _states = new SpeechSegmentState[plan.Segments.Count]; _results = new JsonElement?[plan.Segments.Count]; _tasks = new Task<JsonElement>?[plan.Segments.Count];
    }
    public SpeechPlan Plan { get; }
    public IReadOnlyList<SpeechSegmentState> States { get { lock (_gate) return new ReadOnlyCollection<SpeechSegmentState>(_states.ToArray()); } }
    public Task<JsonElement> SpeakSegmentAsync(int index, Func<SpeechOptions, CancellationToken, Task<JsonElement>> send, CancellationToken cancellationToken = default)
    {
        if (send == null) throw new ArgumentNullException(nameof(send));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (index < 0 || index >= _states.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (_states[index] == SpeechSegmentState.Done) return Task.FromResult(_results[index]!.Value.Clone());
            if (_states[index] == SpeechSegmentState.Unknown) throw new MediaException("speech_result_unknown_do_not_repeat");
            if (_tasks[index] != null) return _tasks[index]!;
            _states[index] = SpeechSegmentState.Running;
            return _tasks[index] = SendAsync(index, send, cancellationToken);
        }
    }
    private async Task<JsonElement> SendAsync(int index, Func<SpeechOptions, CancellationToken, Task<JsonElement>> send, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            var response = await send(new SpeechOptions { Input = Plan.Segments[index], Model = _options.Model, Voice = _options.Voice, Format = _options.Format, Speed = _options.Speed }, token).ConfigureAwait(false);
            if (MediaArtifactParser.Parse(response)?.Kind != MediaKind.Speech) throw new MediaException("speech_invalid_result");
            lock (_gate) { _results[index] = response.Clone(); _states[index] = SpeechSegmentState.Done; }
            return response.Clone();
        }
        catch { lock (_gate) _states[index] = SpeechSegmentState.Unknown; throw; }
    }
}
