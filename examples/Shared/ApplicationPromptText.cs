using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

internal static class ApplicationPromptText
{
    internal static string Format(ApplicationPromptState value)
    {
        if (!value.IsKnown) return "提示词来源：未知。当前会话未提供有效的已应用来源；未知不等于没有提示词。";
        var policy = value.Policy == ApplicationPromptPolicy.Prepend
            ? "prepend：平台段保留在开发者段之前"
            : "fallback：开发者段未设置时使用平台段";
        var source = value.Source switch
        {
            ApplicationPromptSource.None => "未采用平台或开发者应用提示词段（none）",
            ApplicationPromptSource.Platform => "平台应用设置（platform）",
            ApplicationPromptSource.Sdk => "开发者 / Serve 可信宿主（sdk）",
            ApplicationPromptSource.PlatformAndSdk => "平台应用设置 + 开发者 / Serve 可信宿主（platform+sdk）",
            _ => "未知",
        };
        return "当前已应用的提示词来源：" + source + "\n组合策略：" + policy +
            "\n仅显示来源与策略，不返回提示词正文。sdk 指可信宿主段，不是终端可以随意覆盖的配置。";
    }
}
