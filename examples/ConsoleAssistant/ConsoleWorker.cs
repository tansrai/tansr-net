using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Terminal;

namespace ConsoleAssistant;

/// <summary>应用级有界作业调度；每条作业只调用一次公开 SessionRun，不实现模型循环或隐式重试。</summary>
internal static class ConsoleWorker
{
    internal const int MaximumJobsBytes = 32 * 1024 * 1024;
    internal const int MaximumJobs = 1024;

    internal static async Task<int> RunAsync(string jobsPath, int concurrency, CancellationToken stop, Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var output = new Output(write);
        using var connectionLifetime = new CancellationTokenSource();
        ExampleConnection? connection = null;
        IReadOnlyList<Job>? jobs = null;
        var queueStarted = false;
        var exitCode = 1;
        try
        {
            ValidateConcurrency(concurrency);
            // 在产生任何远端副作用前完成整个输入文件的验证；不会执行一半才发现重复 job id。
            var bytes = await BoundedFiles.ReadAsync(jobsPath, MaximumJobsBytes, stop).ConfigureAwait(false);
            jobs = ParseJobs(bytes);
            stop.ThrowIfCancellationRequested();
            // 启动期间响应 stop，运行期保留共用连接直至各 job 完成独立收尾，不提前杀掉 MCP。
            using (stop.Register(connectionLifetime.Cancel))
                connection = await ExampleConnection.ConnectAsync(Environment.GetEnvironmentVariable("TANSR_SERVE_URL"),
                    _ => Task.FromResult(RequiredToken()), Environment.GetEnvironmentVariable("TANSR_ALLOW_HTTP_LOOPBACK") == "1", connectionLifetime.Token).ConfigureAwait(false);
            queueStarted = true;
            exitCode = await RunJobsAsync(jobs, concurrency, connection.Client, connection.NativeTools, stop, output.Write, connection.Observation, connection.Contract).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { output.Emit(new { phase = "worker", state = "stopped_before_queue", code = "cancelled" }); exitCode = 130; }
        catch (Exception error)
        { output.Emit(new { phase = "worker", state = "failed_before_queue", code = ErrorCode(error) }); }
        finally
        {
            if (!queueStarted && jobs is not null)
            {
                foreach (var job in jobs)
                {
                    output.Emit(new { jobId = job.Id, phase = "acceptance", state = "not_sent", code = "worker_not_started" });
                    output.Emit(new { jobId = job.Id, phase = "terminal", state = "not_started" });
                    output.Emit(new { jobId = job.Id, phase = "cleanup", state = "not_needed" });
                }
            }
            // Connection 所有权只在这里释放；所有作业的独立收尾预算已经结束。
            if (connection is not null)
            {
                try { await connection.CloseAsync().ConfigureAwait(false); output.Emit(new { phase = "connection_cleanup", state = "completed" }); }
                catch (Exception error) { output.Emit(new { phase = "connection_cleanup", state = "unconfirmed", code = ErrorCode(error) }); if (exitCode == 0) exitCode = 2; }
            }
            connectionLifetime.Cancel();
        }
        return output.Failed && exitCode == 0 ? 2 : exitCode;
    }

    // 使用公开 SDK 的同一执行路径；独立受控 HTTP/SSE 验证可以借入 client，不改变其所有权。
    internal static async Task<int> RunJobsAsync(IReadOnlyList<Job> jobs, int concurrency, TansrClient client,
        IReadOnlyList<NativeToolBinding>? bindings, CancellationToken stop, Action<string> write, TerminalObservationClient? observation = null, SessionContract sessionContract = SessionContract.Sdk1)
    {
        ValidateConcurrency(concurrency);
        ArgumentNullException.ThrowIfNull(jobs); ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(write);
        var fixedJobs = jobs.ToArray();
        if (fixedJobs.Length is < 1 or > MaximumJobs || fixedJobs.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != fixedJobs.Length)
            throw new WorkerInputException("invalid_worker_jobs");
        var output = new Output(write);
        var completed = new int[fixedJobs.Length];
        var next = -1;
        var defaultModel = Environment.GetEnvironmentVariable("TANSR_MODEL");
        var lanes = Enumerable.Range(0, Math.Min(concurrency, fixedJobs.Length)).Select(async _ =>
        {
            while (!stop.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= fixedJobs.Length) return;
                if (stop.IsCancellationRequested) return;
                completed[index] = await RunJobAsync(fixedJobs[index], defaultModel, client, bindings, stop, output, observation, sessionContract).ConfigureAwait(false) ? 1 : 2;
            }
        }).ToArray();
        await Task.WhenAll(lanes).ConfigureAwait(false);
        for (var i = 0; i < fixedJobs.Length; i++)
        {
            if (completed[i] != 0) continue;
            output.Emit(new { jobId = fixedJobs[i].Id, phase = "acceptance", state = "not_sent", code = "queue_stopped" });
            output.Emit(new { jobId = fixedJobs[i].Id, phase = "terminal", state = "not_started" });
            output.Emit(new { jobId = fixedJobs[i].Id, phase = "cleanup", state = "not_needed" });
        }
        output.Emit(new
        {
            phase = "worker",
            state = stop.IsCancellationRequested ? "stopped" : "finished",
            jobs = fixedJobs.Length,
            succeeded = completed.Count(x => x == 1),
            failed = completed.Count(x => x == 2),
            notStarted = completed.Count(x => x == 0)
        });
        return stop.IsCancellationRequested ? 130 : completed.All(x => x == 1) && !output.Failed ? 0 : 2;
    }

    private static async Task<bool> RunJobAsync(Job job, string? defaultModel, TansrClient client,
        IReadOnlyList<NativeToolBinding>? bindings, CancellationToken stop, Output output, TerminalObservationClient? observation, SessionContract sessionContract)
    {
        AgentSession? session = null; NativeToolHost? tools = null; SessionRun? run = null;
        var creationStarted = false; var acceptanceWritten = false; var terminalKnown = false; var success = false; var cleanupConfirmed = true;
        try
        {
            stop.ThrowIfCancellationRequested();
            creationStarted = true;
            var options = ExampleSessionOptions.Create(job.Model ?? defaultModel, null, NativeToolHost.GetDeclarations(bindings));
            if (sessionContract == SessionContract.Sdk2OffloadV1)
            {
                if (string.IsNullOrEmpty(options.RequestId)) throw new InvalidOperationException("sdk2_worker_original_request_id_required");
                using var hash = SHA256.Create();
                options.RequestId = "worker-" + BitConverter.ToString(hash.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(new[] { options.RequestId, job.Id }))).Replace("-", "").ToLowerInvariant();
            }
            options.Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["worker.job"] = job.Id };
            session = await client.CreateSessionAsync(options, stop).ConfigureAwait(false);
            output.Emit(new { jobId = job.Id, sessionId = session.Id, phase = "session", state = "created" });
            // 无 UI 服务不能竞争共享控制台标题；原生业务工具明确返回不可用，仍提交一次原回执。
            tools = new NativeToolHost(session, "Tansr.Console.Worker",
                (_, ct) => { ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("window_title_unavailable_in_worker"); },
                code => output.Emit(new { jobId = job.Id, sessionId = session.Id, phase = "native_tool", code }), bindings);
            await tools.Ready.ConfigureAwait(false);
            var activeSession = session; var activeTools = tools;
            run = session.StartRun(job.Prompt, observer: (item, ct) => ObserveAsync(job.Id, activeSession, activeTools, item, ct, output), cancellationToken: stop);
            try
            {
                await run.Acceptance.ConfigureAwait(false);
                output.Emit(new { jobId = job.Id, sessionId = session.Id, phase = "acceptance", state = "accepted" });
            }
            catch (Exception error)
            { output.Emit(new { jobId = job.Id, sessionId = session.Id, phase = "acceptance", state = Acceptance(run.AcceptanceState), code = ErrorCode(error) }); }
            acceptanceWritten = true;
            // 即使接纳失败也观察 Completion 的异常；不会因早到的 terminal 或 HTTP ACK 误报成功。
            var result = await run.Completion.ConfigureAwait(false);
            terminalKnown = true;
            success = !result.WasAborted && result.Reason is "completed" or "structured_output";
            output.Emit(new
            {
                jobId = job.Id,
                sessionId = session.Id,
                phase = "terminal",
                state = success ? "succeeded" : "unsuccessful",
                eventType = result.TerminalEvent.Name,
                turnId = result.TurnId,
                reason = result.Reason
            });
        }
        catch (Exception error)
        {
            if (!acceptanceWritten)
                output.Emit(new { jobId = job.Id, sessionId = session?.Id, phase = "acceptance", state = run is null ? "not_sent" : Acceptance(run.AcceptanceState), code = ErrorCode(error) });
            output.Emit(new { jobId = job.Id, sessionId = session?.Id, phase = "terminal", state = "unconfirmed", code = ErrorCode(error) });
            if (session is null && creationStarted)
                output.Emit(new { jobId = job.Id, phase = "session", state = "creation_unconfirmed", code = "do_not_automatically_recreate" });
        }
        finally
        {
            run?.Dispose();
            if (session is not null)
            {
                if (!terminalKnown && run?.AcceptanceState is SessionMessageAcceptance.Accepted or SessionMessageAcceptance.Unconfirmed)
                    cleanupConfirmed &= await CleanupStepAsync(job.Id, session.Id, "interrupt", () => WithBudgetAsync(session.CancelAsync, TimeSpan.FromSeconds(5)), output).ConfigureAwait(false);
                if (tools is not null)
                    cleanupConfirmed &= await CleanupStepAsync(job.Id, session.Id, "native_tools", () => WithBudgetAsync(tools.DrainAsync, TimeSpan.FromSeconds(12)), output).ConfigureAwait(false);
                cleanupConfirmed &= await CleanupStepAsync(job.Id, session.Id, "close", () => WithBudgetAsync(session.CloseAsync, TimeSpan.FromSeconds(10)), output).ConfigureAwait(false);
                var remoteCleanup = "unknown";
                if (observation != null)
                    cleanupConfirmed &= await CleanupStepAsync(job.Id, session.Id, "resources", async () =>
                    {
                        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var settled = await observation.WaitForResourcesAsync(session.Id, TimeSpan.FromSeconds(10), sessionContract: sessionContract, cancellationToken: budget.Token).ConfigureAwait(false);
                        remoteCleanup = settled.State.ToString().ToLowerInvariant();
                    }, output).ConfigureAwait(false);
                tools?.Dispose();
                output.Emit(new { jobId = job.Id, sessionId = session.Id, phase = "cleanup", state = cleanupConfirmed ? "completed" : "unconfirmed", scope = observation == null ? "local_and_close_request" : "local_and_remote_resources", remoteCleanup });
            }
            else output.Emit(new { jobId = job.Id, phase = "cleanup", state = creationStarted ? "unavailable_session_id" : "not_needed" });
        }
        return success && cleanupConfirmed;
    }

    private static async Task ObserveAsync(string jobId, AgentSession session, NativeToolHost tools, AgentEvent item, CancellationToken ct, Output output)
    {
        tools.HandleEvent(item);
        var data = item.Data;
        var body = data.TryGetProperty("payload", out var payload) ? payload : data;
        if (item.Name == "msg.text.delta" && data.TryGetProperty("text", out var text))
            output.Emit(new { jobId, sessionId = session.Id, phase = "text", text = text.GetString() });
        else if (item.Name == "tool.output.delta" && data.TryGetProperty("chunk", out var chunk))
            output.Emit(new { jobId, sessionId = session.Id, phase = "tool_output", text = chunk.GetString() });
        else if (item.Name == "server.permission.request")
        {
            var id = body.GetProperty("requestId").GetString()!;
            var name = body.TryGetProperty("name", out var tool) && tool.ValueKind == JsonValueKind.String ? tool.GetString() : null;
            var allow = name != null && (Environment.GetEnvironmentVariable("TANSR_WORKER_ALLOWED_PERMISSION_TOOLS") ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Contains(name, StringComparer.Ordinal);
            await session.PermissionAsync(id, body.GetProperty("digest").GetString()!, allow, ct).ConfigureAwait(false);
            output.Emit(new { jobId, sessionId = session.Id, phase = "permission", requestId = id, state = allow ? "allowed_by_explicit_worker_policy" : "denied_unattended" });
        }
        else if (item.Name == "server.question.request")
        {
            var id = body.GetProperty("requestId").GetString()!;
            var answers = body.GetProperty("questions").EnumerateArray().Select(question => new QuestionAnswer(
                question.GetProperty("id").GetString()!, Array.Empty<string>(), "无人值守宿主无法回答；请返回可由用户处理的结果。")).ToArray();
            await session.AnswerAsync(id, answers, ct).ConfigureAwait(false);
            output.Emit(new { jobId, sessionId = session.Id, phase = "question", requestId = id, state = "answered_unattended" });
        }
        else if (item.Name is "turn.error" or "server.replay.gap")
            output.Emit(new { jobId, sessionId = session.Id, phase = "event", eventType = item.Name });
    }

    private static async Task<bool> CleanupStepAsync(string jobId, string sessionId, string resource, Func<Task> action, Output output)
    {
        try { await action().ConfigureAwait(false); output.Emit(new { jobId, sessionId, phase = "cleanup_step", resource, state = "completed" }); return true; }
        catch (Exception error) { output.Emit(new { jobId, sessionId, phase = "cleanup_step", resource, state = "unconfirmed", code = ErrorCode(error) }); return false; }
    }

    private static async Task WithBudgetAsync(Func<CancellationToken, Task> action, TimeSpan budget)
    { using var deadline = new CancellationTokenSource(budget); await action(deadline.Token).ConfigureAwait(false); }

    internal static IReadOnlyList<Job> ParseJobs(byte[] bytes)
    {
        if (bytes.Length > MaximumJobsBytes) throw new WorkerInputException("worker_jobs_file_too_large");
        // 异常 UTF-8 与孤立代理项拒绝；不会用替代字符改写作业内容。
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        using var lines = new StringReader(text);
        var result = new List<Job>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = lines.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (result.Count >= MaximumJobs) throw new WorkerInputException("worker_too_many_jobs");
            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new WorkerInputException("worker_job_must_be_object");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                if (property.Name is not ("id" or "prompt" or "model") || !properties.Add(property.Name)) throw new WorkerInputException("worker_job_unknown_or_duplicate_field");
            var id = RequiredText(json.RootElement, "id"); var prompt = RequiredText(json.RootElement, "prompt");
            if (id.Length > 128 || id.Any(char.IsControl)) throw new WorkerInputException("worker_job_invalid_id");
            if (!ids.Add(id)) throw new WorkerInputException("worker_job_duplicate_id");
            string? model = null;
            if (json.RootElement.TryGetProperty("model", out var modelValue))
            {
                if (modelValue.ValueKind != JsonValueKind.Null)
                {
                    model = RequiredText(json.RootElement, "model");
                    if (model.Length > 512 || model.Any(char.IsControl)) throw new WorkerInputException("worker_job_invalid_model");
                }
            }
            // 与 StartRun 的公开请求上限相同；过大整条拒绝，而非截断正文或拆成新轮次。
            if (JsonSerializer.SerializeToUtf8Bytes(new { prompt }).Length > 20 * 1024 * 1024) throw new WorkerInputException("worker_job_prompt_too_large");
            result.Add(new Job(id, prompt, model));
        }
        if (result.Count == 0) throw new WorkerInputException("worker_jobs_empty");
        return result;
    }

    private static string RequiredText(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new WorkerInputException("worker_job_invalid_" + key);
        var text = value.GetString()!;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || ++i == text.Length || !char.IsLowSurrogate(text[i])) throw new WorkerInputException("worker_job_invalid_unicode");
        }
        return text;
    }

    private static void ValidateConcurrency(int concurrency)
    { if (concurrency is < 1 or > 8) throw new WorkerInputException("worker_concurrency_must_be_1_to_8"); }
    private static string RequiredToken() => Environment.GetEnvironmentVariable("TANSR_SESSION_TOKEN") is { Length: > 0 } value ? value : throw new WorkerInputException("missing_TANSR_SESSION_TOKEN");
    private static string Acceptance(SessionMessageAcceptance state) => state switch
    { SessionMessageAcceptance.Accepted => "accepted", SessionMessageAcceptance.Rejected => "rejected", SessionMessageAcceptance.Unconfirmed => "unconfirmed", _ => "not_sent" };
    private static string ErrorCode(Exception error) => error is WorkerInputException input ? input.Code : error is TansrException sdk ? sdk.Code : error is OperationCanceledException ? "cancelled" : error.GetType().Name;
    internal sealed record Job(string Id, string Prompt, string? Model);
    private sealed class WorkerInputException(string code) : Exception(code) { internal string Code { get; } = code; }

    private sealed class Output(Action<string> write)
    {
        private readonly object gate = new();
        private int failed;
        internal bool Failed => Volatile.Read(ref failed) != 0;
        internal void Emit(object value) => Write(JsonSerializer.Serialize(value));
        internal void Write(string value)
        {
            lock (gate)
            {
                try { write(value); }
                catch { Interlocked.Exchange(ref failed, 1); } // 日志故障不能重做模型或业务工具副作用。
            }
        }
    }
}
