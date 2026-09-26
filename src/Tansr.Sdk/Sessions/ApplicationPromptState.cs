using System.Text.Json;

namespace Tansr.Sdk.Sessions;

public enum ApplicationPromptPolicy { Unknown, Fallback, Prepend }

/// <summary>应用级提示词来源。Sdk 指可信开发者或 Serve 宿主提供的 S，不表示终端用户可修改系统提示词。</summary>
public enum ApplicationPromptSource { Unknown, None, Platform, Sdk, PlatformAndSdk }

/// <summary>当前活动会话的应用级提示词来源投影；不包含任何可用于默认展示的提示词正文。</summary>
/// <remarks>Unknown 表示无法观察，不能当作 None。Raw 只保留原字段供显式诊断，可能包含未通过验证的字段，
/// 不应用于默认界面、日志或提示词组装。观察结果不授予终端修改平台或可信宿主配置的权限。</remarks>
public sealed class ApplicationPromptState
{
    private ApplicationPromptState(ApplicationPromptPolicy policy, ApplicationPromptSource source, JsonElement? raw)
    { Policy = policy; Source = source; Raw = raw?.Clone(); }

    public bool IsKnown => Policy != ApplicationPromptPolicy.Unknown && Source != ApplicationPromptSource.Unknown;
    public ApplicationPromptPolicy Policy { get; }
    public ApplicationPromptSource Source { get; }
    public JsonElement? Raw { get; }

    internal static ApplicationPromptState FromMetadata(JsonElement metadata)
    {
        JsonElement? raw = metadata.TryGetProperty("applicationPrompt", out var value) ? value.Clone() : (JsonElement?)null;
        var policy = ApplicationPromptPolicy.Unknown; var source = ApplicationPromptSource.Unknown;
        if (metadata.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.True &&
            raw.HasValue && raw.Value.ValueKind == JsonValueKind.Object)
        {
            var info = raw.Value; var count = 0;
            foreach (var unused in info.EnumerateObject()) count++;
            if (count == 2 && info.TryGetProperty("policy", out var policyValue) && policyValue.ValueKind == JsonValueKind.String &&
                info.TryGetProperty("source", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.String)
            {
                policy = policyValue.GetString() switch
                {
                    "fallback" => ApplicationPromptPolicy.Fallback,
                    "prepend" => ApplicationPromptPolicy.Prepend,
                    _ => ApplicationPromptPolicy.Unknown
                };
                source = sourceValue.GetString() switch
                {
                    "none" => ApplicationPromptSource.None,
                    "platform" => ApplicationPromptSource.Platform,
                    "sdk" => ApplicationPromptSource.Sdk,
                    "platform+sdk" => ApplicationPromptSource.PlatformAndSdk,
                    _ => ApplicationPromptSource.Unknown
                };
            }
        }
        if (policy == ApplicationPromptPolicy.Unknown || source == ApplicationPromptSource.Unknown ||
            policy == ApplicationPromptPolicy.Fallback && source == ApplicationPromptSource.PlatformAndSdk)
        { policy = ApplicationPromptPolicy.Unknown; source = ApplicationPromptSource.Unknown; }
        return new ApplicationPromptState(policy, source, raw);
    }

    /// <summary>Only validated enum names are formatted; diagnostic Raw is never included.</summary>
    public override string ToString() => IsKnown ? Policy + "/" + Source : "Unknown";
}
