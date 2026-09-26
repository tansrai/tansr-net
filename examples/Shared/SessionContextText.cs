using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// Read-only presentation of the SDK's metadata.context. No token accounting or model inference.
internal static class SessionContextText
{
    internal static string Format(SessionMetadata metadata) =>
        "会话：" + Clean(metadata.Id) + "\n状态：" + Clean(metadata.StatusName) +
        "\n实时会话：" + (metadata.IsLive ? "是" : "否") + "\n\n" + FormatContext(metadata.Context);

    internal static string FormatContext(JsonElement? context)
    {
        var lines = new List<string>();
        var selected = Field(context, "selected");
        lines.Add("下一轮模型（selected）");
        AddModel(lines, selected);
        lines.Add(""); lines.Add("当前运行模型（active）");
        var active = Field(context, "active");
        if (active?.ValueKind == JsonValueKind.Null) lines.Add("无运行轮（active = null）");
        else AddModel(lines, active);

        var budget = Field(context, "budget");
        lines.Add(""); lines.Add("核心上下文预算（budget）");
        var source = Text(Field(budget, "source"));
        lines.Add("预算来源：" + (source == "local_estimate" ? "local_estimate（Serve/core 本地估算，非平台计费用量）" : source));
        lines.Add("设置来源：" + Text(Field(budget, "settingsSource")));
        lines.Add("物理窗口：" + Tokens(budget, "physicalWindowTokens") + "；有效窗口：" + Tokens(budget, "effectiveWindowTokens"));
        lines.Add("当前输入估算：" + Tokens(budget, "estimatedInputTokens"));
        lines.Add("输出预留：" + Tokens(budget, "outputReserveTokens") + "；思考预留：" + Tokens(budget, "thinkingReserveTokens"));
        lines.Add("剩余预算：" + Tokens(budget, "remainingTokens", allowNegative: true) + "；能否容纳：" + Boolean(Field(budget, "fits")));
        var thresholds = Field(budget, "thresholds");
        lines.Add("阈值：微压缩 " + Tokens(thresholds, "microcompactAt") + "；警告 " + Tokens(thresholds, "warningAt") +
            "；自动压缩 " + Tokens(thresholds, "autoCompactAt") + "；阻断 " + Tokens(thresholds, "blockingAt"));
        lines.Add("摘要预留：" + Tokens(thresholds, "summaryReserveTokens"));
        lines.Add("历史口径：" + Text(Field(context, "history")) + "；采样时间：" + Timestamp(Field(context, "sampledAt")));

        lines.Add(""); lines.Add("最近主调用用量（lastUsage；独立观测，不代表当前上下文占用）");
        var lastUsage = Field(context, "lastUsage");
        if (lastUsage?.ValueKind == JsonValueKind.Null) lines.Add("尚无主调用用量观测（lastUsage = null）");
        else
        {
            lines.Add("用量归属模型：" + ModelIdentity(Field(lastUsage, "model")));
            lines.Add("观测时间：" + Timestamp(Field(lastUsage, "observedAt")));
            var usage = Field(lastUsage, "usage");
            lines.Add("输入用量：" + Tokens(usage, "inputTokens") + "；输出用量：" + Tokens(usage, "outputTokens"));
            lines.Add("缓存读取用量：" + Tokens(usage, "cacheReadInputTokens") + "；缓存创建用量：" + Tokens(usage, "cacheCreationInputTokens"));
            lines.Add("推理输出用量：" + Tokens(usage, "reasoningOutputTokens") + "；缓存删除观测量：" + Tokens(usage, "cacheDeletedInputTokens"));
        }
        return string.Join("\n", lines);
    }

    private static void AddModel(List<string> lines, JsonElement? state)
    {
        lines.Add("模型：" + ModelIdentity(Field(state, "model")));
        var alias = Field(state, "alias");
        lines.Add("选择别名：" + (alias?.ValueKind == JsonValueKind.Null ? "无" : Text(alias)));
        lines.Add("已知上下文窗口：" + Tokens(state, "contextWindowTokens"));
    }

    private static string ModelIdentity(JsonElement? model) =>
        "provider=" + Text(Field(model, "provider")) + " / model=" + Text(Field(model, "model"));
    private static JsonElement? Field(JsonElement? parent, string name) =>
        parent?.ValueKind == JsonValueKind.Object && parent.Value.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
    private static string Text(JsonElement? value) => value?.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.Value.GetString())
        ? Clean(value.Value.GetString()!) : "未知";
    private static string Clean(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    private static string Tokens(JsonElement? parent, string key, bool allowNegative = false)
    {
        var value = Field(parent, key);
        return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number) &&
            number <= 9007199254740991L && number >= (allowNegative ? -9007199254740991L : 0)
            ? number.ToString(CultureInfo.InvariantCulture) + " tokens" : "未知";
    }
    private static string Boolean(JsonElement? value) => value?.ValueKind == JsonValueKind.True ? "是" : value?.ValueKind == JsonValueKind.False ? "否" : "未知";
    private static string Timestamp(JsonElement? value)
    {
        if (value?.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt64(out var timestamp) || timestamp < 0) return "未知";
        try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return "未知"; }
    }
}
