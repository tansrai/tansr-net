using System.Globalization;
using System.Text;

namespace Tansr.Sdk.Views;

/// <summary>无框架依赖的示例呈现；原生应用可以直接绑定快照而采用自己的布局。</summary>
public static class SessionViewTextFormatter
{
    public static string Format(SessionViewSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var text = new StringBuilder();
        foreach (var message in snapshot.Messages)
        {
            text.Append('[').Append(message.Role).AppendLine("]");
            foreach (var part in message.Parts)
            {
                if (part.Kind == "thinking") text.Append("[thinking] ");
                if (part.Kind == "toolCall")
                {
                    var tool = snapshot.Tools.FirstOrDefault(x => x.Id == part.ToolId);
                    if (tool == null) continue;
                    text.Append('[').Append(tool.Name).Append(": ").Append(tool.Status).AppendLine("]");
                    if (!string.IsNullOrEmpty(tool.Progress)) text.AppendLine(tool.Progress);
                    if (!string.IsNullOrEmpty(tool.OutputTail)) text.AppendLine(tool.OutputTail);
                    // 已展示输出不再把终局全文追加一次；结构化结果仍在 snapshot 中供专用渲染器消费。
                    else if (!string.IsNullOrEmpty(tool.ResultText)) text.AppendLine(tool.ResultText);
                    if (tool.Error != null) text.AppendLine(tool.Error);
                    if (tool.OutputTruncated) text.AppendLine("[output truncated]");
                }
                else text.AppendLine(part.Text);
                if (part.Truncated) text.AppendLine("[text truncated]");
            }
        }
        foreach (var todo in snapshot.Todos) text.Append("[todo:").Append(todo.Status).Append("] ").AppendLine(todo.Content);
        foreach (var agent in snapshot.Agents) text.Append("[agent:").Append(agent.Id).Append('/').Append(agent.Status).Append("] ").AppendLine(agent.Message);
        foreach (var notice in snapshot.Notices) text.Append('[').Append(notice.Code).Append("] ").AppendLine(notice.Message);
        if (snapshot.LastError != null) text.Append("[error:").Append(snapshot.LastError.Code).Append("] ").AppendLine(snapshot.LastError.Message);
        if (snapshot.HasEventGap) text.AppendLine("[event gap: reload authoritative history before treating this view as complete]");
        if (snapshot.PresentationTruncated) text.AppendLine("[presentation truncated: full history is separate]");
        text.Append("[status:").Append(snapshot.Status).Append("] tokens=")
            .Append(snapshot.SessionTokens.ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }
}
