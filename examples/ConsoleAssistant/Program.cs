using System.Runtime.InteropServices;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace ConsoleAssistant;

internal static class Program
{
    private static readonly object OutputLock = new();

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Tansr 原生控制台\n环境：TANSR_SERVE_URL、TANSR_SESSION_TOKEN；可选 TANSR_ALLOW_HTTP_LOOPBACK=1、TANSR_RESUME_SESSION、TANSR_MODEL。\n命令：普通文字发送；/history /meta /compact /checkpoint /checkpoints /cancel /requests /allow <requestId> /deny <requestId> /answer <requestId> <答案JSON数组> /quit。\n无界面 --once <prompt> 会拒绝审批、以明确的无人值守说明回答提问，等待终态后关闭。Ctrl+C / SIGTERM 中断当前工作并有界收尾。票据不写入文件或日志。");
            return 0;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var term = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
        TansrClient? client = null; AgentSession? session = null; Task? observation = null; NativeToolHost? nativeTools = null;
        using var view = new SessionView();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = args.Length >= 2 && args[0] == "--once";
        try
        {
            var baseUrl = Required("TANSR_SERVE_URL");
            client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri(baseUrl),
                AllowInsecureLoopback = Environment.GetEnvironmentVariable("TANSR_ALLOW_HTTP_LOOPBACK") == "1",
                TokenProvider = _ => Task.FromResult(Required("TANSR_SESSION_TOKEN")),
            });
            session = await client.CreateSessionAsync(new CreateSessionOptions
            {
                Model = Environment.GetEnvironmentVariable("TANSR_MODEL"),
                ResumeSessionId = Environment.GetEnvironmentVariable("TANSR_RESUME_SESSION"),
                ClientTools = Environment.GetEnvironmentVariable("TANSR_RESUME_SESSION") == null ? NativeToolHost.Declarations : null,
            }, stop.Token);
            var active = session;
            nativeTools = new NativeToolHost(session, "Tansr.Console", (title, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (Console.IsOutputRedirected || !OperatingSystem.IsWindows()) throw new InvalidOperationException("console_window_title_unavailable");
                Console.Title = title; return Task.CompletedTask;
            }, Write);
            Write("session=" + session.Id);
            observation = ObserveAsync(active, view, nativeTools, once, completed, stop.Token);
            if (once)
            {
                var prompt = string.Join(" ", args.Skip(1));
                await active.SendAsync(prompt, stop.Token);
                return await completed.Task.WaitAsync(stop.Token) ? 0 : 2;
            }
            while (!stop.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stop.Token);
                if (line == null || line == "/quit") break;
                try
                {
                    if (line == "/history") Write((await active.GetHistoryAsync(stop.Token)).GetRawText());
                    else if (line == "/meta") Write((await active.GetMetadataAsync(stop.Token)).GetRawText());
                    else if (line == "/compact") Write((await active.CompactAsync(cancellationToken: stop.Token)).GetRawText());
                    else if (line == "/checkpoint") Write((await active.CheckpointAsync(cancellationToken: stop.Token)).GetRawText());
                    else if (line == "/checkpoints") Write((await active.ListCheckpointsAsync(stop.Token)).GetRawText());
                    else if (line == "/cancel") Write((await active.CancelAsync(stop.Token)).GetRawText());
                    else if (line == "/requests") foreach (var pending in view.Snapshot.PendingRequests) Write(pending.Kind + " " + pending.Id + " " + pending.Data.GetRawText());
                    else if (line.StartsWith("/allow ", StringComparison.Ordinal) || line.StartsWith("/deny ", StringComparison.Ordinal))
                    {
                        var allow = line.StartsWith("/allow ", StringComparison.Ordinal); var id = line.Substring(allow ? 7 : 6).Trim();
                        var request = view.Snapshot.PendingRequests.SingleOrDefault(x => x.Id == id && x.Kind == "permission") ?? throw new InvalidOperationException("permission_request_not_found");
                        Write((await active.PermissionAsync(id, request.Data.GetProperty("digest").GetString()!, allow, stop.Token)).GetRawText());
                    }
                    else if (line.StartsWith("/answer ", StringComparison.Ordinal))
                    {
                        var rest = line.Substring(8); var split = rest.IndexOf(' '); if (split <= 0) throw new InvalidOperationException("answer_requires_request_id_and_json");
                        var id = rest.Substring(0, split);
                        if (!view.Snapshot.PendingRequests.Any(x => x.Id == id && x.Kind == "question")) throw new InvalidOperationException("question_request_not_found");
                        using var document = JsonDocument.Parse(rest.Substring(split + 1));
                        var answers = document.RootElement.EnumerateArray().Select(x => new QuestionAnswer(x.GetProperty("questionId").GetString()!,
                            x.GetProperty("selectedOptionIds").EnumerateArray().Select(y => y.GetString()!).ToArray(),
                            x.TryGetProperty("freeText", out var free) ? free.GetString() : null)).ToArray();
                        Write((await active.AnswerAsync(id, answers, stop.Token)).GetRawText());
                    }
                    else if (line.StartsWith("/", StringComparison.Ordinal)) Write("未知命令；使用 --help 查看入口。");
                    else if (!string.IsNullOrWhiteSpace(line))
                    { await active.SendAsync(line, stop.Token); Write("accepted（不等于模型完成或耐久提交）"); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error) { Write("error=" + ErrorCode(error) + "；请求结果未知时先查状态，不自动重发。"); }
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
        catch (Exception error) { Write("error=" + ErrorCode(error)); return 1; }
        finally
        {
            if (session != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (stop.IsCancellationRequested) await session.CancelAsync(timeout.Token);
                    await session.CloseAsync(timeout.Token);
                    if (nativeTools != null) await nativeTools.DrainAsync(timeout.Token);
                    Write("closed（未删除历史；远端更细的资源清理事实以服务合同为准）");
                }
                catch (Exception error) { Write("close_unconfirmed=" + ErrorCode(error)); }
            }
            stop.Cancel();
            if (observation != null) { try { await observation; } catch (Exception error) { Write("observer=" + ErrorCode(error)); } }
            nativeTools?.Dispose(); client?.Dispose(); Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task ObserveAsync(AgentSession session, SessionView view, NativeToolHost tools, bool unattended,
        TaskCompletionSource<bool> completed, CancellationToken token)
    {
        try
        {
            await session.ObserveAsync(async (item, ct) =>
            {
                view.Apply(item);
                tools.HandleEvent(item);
                var data = item.Data; var type = data.TryGetProperty("type", out var t) ? t.GetString() ?? item.Name : item.Name;
                var body = data.TryGetProperty("payload", out var payload) ? payload : data;
                if (type == "msg.text.delta" && data.TryGetProperty("text", out var text)) Write(text.GetString() ?? "");
                else if (type == "tool.output.delta" && data.TryGetProperty("chunk", out var chunk)) Write("tool: " + chunk.GetString());
                else if (type == "server.permission.request" || type == "server.question.request")
                {
                    var id = body.GetProperty("requestId").GetString()!; Write(type + " " + id + " " + body.GetRawText());
                    if (unattended && type == "server.permission.request") await session.PermissionAsync(id, body.GetProperty("digest").GetString()!, false, ct);
                    else if (unattended)
                        await session.AnswerAsync(id, body.GetProperty("questions").EnumerateArray().Select(q => new QuestionAnswer(q.GetProperty("id").GetString()!, Array.Empty<string>(), "无人值守宿主无法回答；请返回可由用户处理的结果。")).ToArray(), ct);
                }
                else if (type == "turn.completed" || type == "turn.aborted")
                {
                    Write(type + " " + data.GetRawText());
                    var reason = data.TryGetProperty("reason", out var terminal) ? terminal.GetString() : null;
                    completed.TrySetResult(type == "turn.completed" && (reason == "completed" || reason == "structured_output"));
                }
                else if (type == "turn.error" || type == "server.replay.gap") Write(type + " " + data.GetRawText());
            }, token);
            if (!completed.Task.IsCompleted) completed.TrySetException(new InvalidOperationException("event_stream_ended_before_terminal"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { completed.TrySetCanceled(token); }
        catch (Exception error) { completed.TrySetException(error); Write("event_source=" + ErrorCode(error)); }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("missing_" + name);
    private static string ErrorCode(Exception error) => error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name;
    private static void Write(string value) { lock (OutputLock) Console.WriteLine(value); }
}
