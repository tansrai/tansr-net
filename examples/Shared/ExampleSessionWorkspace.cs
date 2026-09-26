using System.Globalization;
using System.IO;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Examples;

// 三个示例共用公开 SDK 控制面；不实现网络、重试、模型循环或第二套任务状态机。
internal sealed class ExampleSessionWorkspace
{
    private readonly TansrClient client;
    private readonly AgentSession session;
    private readonly Func<SessionViewSnapshot> view;
    private readonly Func<string> localServices;
    private readonly CancellationToken lifetime;
    private readonly Func<int, string, CancellationToken, Task<string>>? storage;
    internal ExampleSessionWorkspace(TansrClient client, AgentSession session, Func<SessionViewSnapshot> view,
        Func<string> localServices, CancellationToken lifetime, Func<int, string, CancellationToken, Task<string>>? storage = null)
    { this.client = client; this.session = session; this.view = view; this.localServices = localServices; this.lifetime = lifetime; this.storage = storage; }

    internal static readonly string[] Actions =
    {
        "能力与接入状态", "工具 / Task / 待办 / 用量", "会话列表（参数：页码）", "历史分页（参数：页码）",
        "列出快照", "创建快照（参数：标签）", "恢复快照（参数：ID）", "删除快照（参数：ID）",
        "从快照分叉（参数：ID）", "改变服务工作区（参数：受信路径）", "压缩（参数：补充指示）",
        "打开 SDK1 本地镜像配置（参数：受信 JSON 路径）", "启用 SDK1 本地镜像", "关闭并删除 SDK1 本地镜像", "读取 SDK1 本地镜像摘要",
        "连接 SDK2 本地档案（参数：受信 JSON 路径）", "同步 SDK2 档案与原 ACK", "读取 SDK2 本地档案", "停止 SDK2 本机档案连接", "SDK2 档案状态",
        "重查并清理 SDK1 镜像临时快照",
        "打开缓存连续性配置（参数：受信 JSON 路径）", "新建逻辑缓存绑定", "使用原票据续接逻辑缓存", "查询缓存原请求", "读取缓存诊断", "显式关闭逻辑缓存", "本机缓存接入状态",
    };

    internal async Task<string> ExecuteAsync(int action, string argument, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var ct = stop.Token; ct.ThrowIfCancellationRequested();
        switch (action)
        {
            case 0:
                return "Serve 能力（仅此响应中声明的能力）：\n" + (await client.GetSessionCapabilitiesAsync(ct).ConfigureAwait(false)).GetRawText() +
                    "\n\n本机装配：\n" + localServices() + "\n技能、hooks、子代理策略由受信 Serve profile 装配；能力声明不是终端执行许可。";
            case 1: return DescribeActivity(view());
            case 2: return (await client.ListSessionsAsync(25, Page(argument), ct).ConfigureAwait(false)).GetRawText();
            case 3: return (await session.GetHistoryAsync(Page(argument), 25, ct).ConfigureAwait(false)).GetRawText();
            case 4: return (await session.ListCheckpointsAsync(ct).ConfigureAwait(false)).GetRawText();
            case 5: return (await session.CheckpointAsync(Empty(argument), ct).ConfigureAwait(false)).GetRawText();
            case 6: return (await session.RestoreCheckpointAsync(Required(argument), cancellationToken: ct).ConfigureAwait(false)).GetRawText();
            case 7: return (await session.DeleteCheckpointAsync(Required(argument), ct).ConfigureAwait(false)).GetRawText();
            case 8:
                var fork = await session.ForkAsync(Required(argument), cancellationToken: ct).ConfigureAwait(false);
                // 不自动替换原会话，也不触发 fork 的工具；用户显式断开后恢复该 id。
                return "forkSessionId=" + fork.Id + "\n原会话保持连接。要使用分叉，请先断开，再在恢复会话 ID 中填入此值。";
            case 9: return (await session.SetCwdAsync(Required(argument), ct).ConfigureAwait(false)).GetRawText();
            case 10: return (await session.CompactAsync(new CompactOptions { Instructions = Empty(argument) }, ct).ConfigureAwait(false)).GetRawText();
            default:
                if (action >= 11 && action <= 27 && storage != null) return await storage(action, argument, ct).ConfigureAwait(false);
                throw new InvalidOperationException("select_workspace_action");
        }
    }
    internal async Task ExportAsync(string checkpointId, string destination, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        stop.Token.ThrowIfCancellationRequested();
        var bytes = await session.ExportCheckpointAsync(Required(checkpointId), stop.Token).ConfigureAwait(false);
        // 用户选定新文件；不覆盖既有档案，失败保留服务端原快照。
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, 0, bytes.Length, stop.Token).ConfigureAwait(false); stream.Flush(true);
    }
    internal async Task<string> ImportAsync(string source, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var bytes = await BoundedFiles.ReadAsync(source, 8 * 1024 * 1024, stop.Token).ConfigureAwait(false);
        return (await session.ImportCheckpointAsync(bytes, cancellationToken: stop.Token).ConfigureAwait(false)).GetRawText();
    }
    internal static string DescribeActivity(SessionViewSnapshot snapshot)
    {
        var text = new StringBuilder("运行状态：").AppendLine(snapshot.Status)
            .Append("事件序号：").AppendLine(snapshot.LastSequence?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
            .Append("本轮 token：").Append(snapshot.TurnTokens).Append("；本会话累计 token：").Append(snapshot.SessionTokens)
            .Append("；用量报告次数：").AppendLine(snapshot.UsageRequests.ToString(CultureInfo.InvariantCulture))
            .AppendLine("累计 token 不是当前上下文，也不是最终费用；上下文请查看“状态”。");
        foreach (var tool in snapshot.Tools) text.Append("工具 ").Append(tool.Name).Append(" · ").Append(tool.Status).Append(" · ").AppendLine(tool.Progress ?? "");
        foreach (var task in snapshot.Agents) text.Append("Task ").Append(task.Id).Append(" · ").Append(task.Status).Append(" · ").AppendLine(task.Message ?? "");
        foreach (var todo in snapshot.Todos) text.Append("待办 ").Append(todo.Status).Append(" · ").AppendLine(todo.Content);
        foreach (var notice in snapshot.Notices) text.Append(notice.IsError ? "错误 " : "提示 ").Append(notice.Code).Append(" · ").AppendLine(notice.Message ?? "");
        if (snapshot.HasEventGap) text.AppendLine("存在事件缺口；此投影不能代替服务端权威历史。");
        if (snapshot.PresentationTruncated) text.AppendLine("界面按容量截断，完整历史仍需从权威存储读取。");
        return text.ToString();
    }
    private static int Page(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page > int.MaxValue / 25)
            throw new InvalidOperationException("invalid_page");
        return page * 25;
    }
    private static string Required(string text) => Empty(text) ?? throw new InvalidOperationException("workspace_argument_required");
    private static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
