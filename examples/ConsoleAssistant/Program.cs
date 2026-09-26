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
        if (args.Length == 1 && args[0] == "--mcp")
        {
            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            Console.CancelKeyPress += handler;
            try { await new NativeMcpServer().RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), shutdown.Token); return 0; }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); return 2; }
            finally { Console.CancelKeyPress -= handler; }
        }
        if (args.Length == 1 && args[0] == "--mcp-client")
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { Console.WriteLine((await NativeMcpDemo.RunAsync(deadline.Token)).GetRawText()); return 0; }
            catch (Exception error) { Console.Error.WriteLine(ErrorCode(error)); return 2; }
        }
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Tansr 原生控制台\n环境：TANSR_SERVE_URL、TANSR_SESSION_TOKEN；可选 TANSR_ALLOW_HTTP_LOOPBACK=1、TANSR_RESUME_SESSION、TANSR_MODEL。\n命令：普通文字发送；/history /meta /compact /checkpoint /checkpoints /cancel /requests /allow <requestId> /deny <requestId> /answer <requestId> <答案JSON数组> /quit。\n无界面 --once <prompt> 会拒绝审批、以明确的无人值守说明回答提问，等待终态后关闭。Ctrl+C / SIGTERM 中断当前工作并有界收尾。票据不写入文件或日志。");
            Console.WriteLine("草稿：/draft <全文>、/draft-file <UTF8文件>、/draft、/send-draft；同轮：/insert <全文>、/insert-draft、/input-status、/input-retry（原键）；离线：--offline、/offline。多会话：--worker <jobs.jsonl> [--concurrency 1..8]。");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--offline")
        {
            try { var saved = LocalConversationState.ForApplication("console").Load(); Write(saved.OfflineText); Write("本机草稿（未发送）：\n" + saved.Draft); return 0; }
            catch (Exception error) { Write("local_state=" + ErrorCode(error)); return 1; }
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var term = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
        if (args.Length > 0 && args[0] == "--worker")
        {
            try
            {
                if (args.Length != 2 && (args.Length != 4 || args[2] != "--concurrency")) throw new InvalidOperationException("worker_requires_jobs_file_and_optional_concurrency");
                var concurrency = args.Length == 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 2;
                return await ConsoleWorker.RunAsync(args[1], concurrency, stop.Token, Write);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
            catch (Exception error) { Write("worker=" + ErrorCode(error)); return 1; }
            finally { Console.CancelKeyPress -= cancel; }
        }
        MediaWorkspace? media = null; ConsoleMediaCommands? mediaCommands = null; ExampleConnection? connection = null; TansrClient? client = null; AgentSession? session = null; Task? observation = null; NativeToolHost? nativeTools = null;
        using var view = new SessionView();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = args.Length >= 2 && args[0] == "--once";
        var local = LocalConversationState.ForApplication("console");
        string draft = "";
        IDisposable? localSubscription = null;
        void SaveLocal(string? history = null) => local.Save(Environment.GetEnvironmentVariable("TANSR_SERVE_URL") ?? "local-owned-serve", session?.Id ?? "", draft, SessionViewTextFormatter.Format(view.Snapshot), history);
        try
        {
            var saved = local.Load(); draft = saved.Draft;
            if (saved.Input != null && !saved.Input.CanStartAnother &&
                (saved.SessionId != Environment.GetEnvironmentVariable("TANSR_RESUME_SESSION") || saved.Endpoint != (Environment.GetEnvironmentVariable("TANSR_SERVE_URL") ?? "local-owned-serve")))
                throw new InvalidOperationException("unresolved_input_requires_original_serve_and_resume_session");
            connection = await ExampleConnection.ConnectAsync(Environment.GetEnvironmentVariable("TANSR_SERVE_URL"),
                _ => Task.FromResult(Required("TANSR_SESSION_TOKEN")), Environment.GetEnvironmentVariable("TANSR_ALLOW_HTTP_LOOPBACK") == "1", stop.Token);
            client = connection.Client;
            session = await client.CreateSessionAsync(new CreateSessionOptions
            {
                Model = Environment.GetEnvironmentVariable("TANSR_MODEL"),
                ResumeSessionId = Environment.GetEnvironmentVariable("TANSR_RESUME_SESSION"),
                ClientTools = Environment.GetEnvironmentVariable("TANSR_RESUME_SESSION") == null ? NativeToolHost.GetDeclarations(connection.NativeTools) : null,
            }, stop.Token);
            var active = session;
            var input = new TurnInputEditor(session, saved.Endpoint == (Environment.GetEnvironmentVariable("TANSR_SERVE_URL") ?? "local-owned-serve") ? saved.Input : null, local.SaveInput);
            var controls = connection.SessionControl == null ? null : new ExampleSessionControls(connection.SessionControl,
                Environment.GetEnvironmentVariable("TANSR_SERVE_URL") ?? "local-owned-serve", session.Id, local.PathName);
            SaveLocal();
            long lastSavedTicks = 0;
            localSubscription = view.Subscribe(_ =>
            {
                var now = DateTime.UtcNow.Ticks; var previous = Interlocked.Read(ref lastSavedTicks);
                if (now - previous < TimeSpan.FromMilliseconds(750).Ticks || Interlocked.CompareExchange(ref lastSavedTicks, now, previous) != previous) return;
                try { SaveLocal(); } catch (Exception error) { Write("local_state_save_failed=" + ErrorCode(error)); }
            });
            media = new MediaWorkspace(session); mediaCommands = new ConsoleMediaCommands(media, view, Write, text => { draft = text; SaveLocal(); });
            nativeTools = new NativeToolHost(session, "Tansr.Console", (title, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (Console.IsOutputRedirected || !OperatingSystem.IsWindows()) throw new InvalidOperationException("console_window_title_unavailable");
                Console.Title = title; return Task.CompletedTask;
            }, Write, connection.NativeTools);
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
                    if (line == "/controls") Write("preview：/control <0..9> [参数]；" + string.Join("；", ExampleSessionControls.Actions.Select((name, index) => index + "=" + name)) + (controls == null ? "\n未启用；需TANSR_TERMINAL_PREVIEW=1及可信scope文件。" : "\n" + controls.DescribePending()));
                    else if (line.StartsWith("/control ", StringComparison.Ordinal))
                    {
                        if (controls == null) throw new InvalidOperationException("terminal_preview_not_configured");
                        var parts = line.Substring(9).Split(new[] { ' ' }, 2); var action = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        Write((await controls.ExecuteAsync(action, parts.Length == 2 ? parts[1] : "", stop.Token)).GetRawText());
                    }
                    else if (line == "/offline") Write(local.Snapshot.OfflineText);
                    else if (line == "/draft") Write("draft（未发送）：\n" + draft);
                    else if (line.StartsWith("/draft ", StringComparison.Ordinal)) { draft = line.Substring(7); SaveLocal(); Write("draft_saved"); }
                    else if (line.StartsWith("/draft-file ", StringComparison.Ordinal))
                    { draft = new System.Text.UTF8Encoding(false, true).GetString(await BoundedFiles.ReadAsync(line.Substring(12), 1024 * 1024, stop.Token)); SaveLocal(); Write("draft_saved_no_truncation"); }
                    else if (line == "/send-draft")
                    { if (string.IsNullOrWhiteSpace(draft)) throw new InvalidOperationException("draft_missing"); await active.SendAsync(draft, stop.Token); view.AppendUserMessage(draft); draft = ""; SaveLocal(); Write("draft_accepted"); }
                    else if (line == "/insert-draft" || line.StartsWith("/insert ", StringComparison.Ordinal))
                    { var text = line == "/insert-draft" ? draft : line.Substring(8); if (line != "/insert-draft") draft = text; SaveLocal(); Write(await input.InsertAsync(text, stop.Token)); if (input.Current?.Outcome == "accepted" && draft == text) draft = ""; SaveLocal(); }
                    else if (line == "/input-status") Write(await input.QueryAsync(stop.Token));
                    else if (line == "/input-retry") Write(await input.RetryOriginalAsync(stop.Token));
                    else if (await mediaCommands.TryHandleAsync(line, stop.Token)) { }
                    else if (line == "/history") { var history = (await active.GetHistoryAsync(stop.Token)).GetRawText(); SaveLocal(history); Write(history); }
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
                    { draft = line; SaveLocal(); await active.SendAsync(line, stop.Token); view.AppendUserMessage(line); draft = ""; SaveLocal(); Write("accepted（不等于模型完成或耐久提交）"); }
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
            localSubscription?.Dispose();
            try { if (session != null) SaveLocal(); } catch (Exception error) { Write("local_state_save_failed=" + ErrorCode(error)); }
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
            media?.Dispose(); nativeTools?.Dispose(); client?.Dispose();
            if (connection != null) { try { await connection.CloseAsync(); } catch (Exception error) { Write("local_serve_cleanup_unconfirmed=" + ErrorCode(error)); } }
            Console.CancelKeyPress -= cancel;
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
