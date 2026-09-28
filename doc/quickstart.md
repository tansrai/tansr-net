# C# 快速接入

本仓仍是未上架的开发候选。本次发行版本为 `0.1.0.2`；上架前请使用已核验的本地包。正式进度见[开发记录](development.md)；包签名、渠道所有权与上传单独结算。

## 选择与安装

只连接远端 Serve 的 .NET 服务引用 `Tansr.Sdk`。需要 Windows 本地文件/进程、SQLite、DPAPI 或受控本地 Serve 的应用引用 `Tansr.Sdk.Windows`，后者自动带入核心包。现代示例使用 .NET 10；既有 WinForms 使用 .NET Framework 4.8，无须换 UI 框架。SDK 包不携带完整运行时。

将本次批准的两个 `.nupkg` 放在本地候选目录，用独立 `NuGet.Config` 将 `Tansr.*` 映射到该目录，其余依赖映射到 NuGet.org。完整可复制的生成方式见 `scripts/test-packages.ps1`；不要将旧同版本预览包缓存当成本批产物。

```powershell
dotnet add YourApp.csproj package Tansr.Sdk.Windows --version 0.1.0.2
dotnet restore YourApp.csproj --configfile NuGet.Config --packages .packages
```

此命令在已有本地源配置后执行。`net48` 消费需系统 .NET Framework；现代应用可选择 framework-dependent 或 self-contained。初版实际候选门以 Windows x64 为准，其它 RID 不能据编译结果宣称支持。

## 一轮与多轮

```csharp
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

using var client = new TansrClient(new TansrClientOptions
{
    BaseUri = new Uri("https://your-serve.example"),
    TokenProvider = ct => YourLoginService.GetShortLivedTokenAsync(ct),
});
var session = await client.CreateSessionAsync(new CreateSessionOptions(), cancellationToken);
using var run = session.StartRun("查询这笔订单", observer: (item, ct) =>
{
    // 在后台接收不可变事件；WPF 用 Dispatcher，WinForms 用 BeginInvoke 更新控件。
    return Task.CompletedTask;
}, cancellationToken: cancellationToken);
await run.Acceptance;
var result = await run.Completion;
Console.WriteLine($"{result.TurnId}: {result.Reason}; aborted={result.WasAborted}");
```

`YourLoginService` 是应用自身的认证接入，不是 SDK 中的隐藏服务。终端只拿短期用户令牌，长期 appkey 留在开发者服务端。`TokenProvider` 每次提供当前票据；换票不需要重建会话。`StartRun` 为独占该会话写入的宿主收取原轮终值，不替并发客户端发明消息归属。

开发者部署的 Serve 若另要求独立用户认证头，可显式配置 `TansrClientOptions.AdditionalRequestHeaders`。仅允许有界的 `x-` 头，在构造客户端时复制；不能覆盖 Bearer、Host 或 Cookie，也不自动发送 `PrincipalProvider` 的本地身份字符串。不要从聊天、模型参数或未核实的用户声明生成这些头。额外票变更须重新配置客户端并恢复原会话；普通 Bearer 续期仍沿 `TokenProvider`。

继续对话复用同一个 `session`。低阶 `SendAsync` 只返回接纳；自行消费持续 SSE 的宿主可用它，但不能把 HTTP 202 当成回复完成。`run.CancelObservation()`、取消本地等待、断开网络不等于中断 Serve；要中断，调用 `session.CancelAsync`。`CloseAsync` 的受理也不自动证明远端工具、存储和记忆已排空，按 Serve 实际清理回执处理。

SDK 默认保留 SDK1 合同；SDK2 档案卸载使用显式 `SessionContract.Sdk2OffloadV1`、可信主体/scope 与已发现能力。协议发现失败不能静默换成 SDK1 或换存储方式。[终端服务](terminal-services.md)说明候选能力协商；[设备记忆](device-memory.md)说明可信装配及 create/reopen。

## 工具、记忆和界面

Serve/kernel 管理循环、上下文、权限与记忆决策。终端使用 `DeviceSessionHost`、Windows 后端、耐久执行日志和已有执行通知接线处理已授权本机操作，不需要应用自己写 polling、SSE、心跳或 ACK 状态机。SDK不执行模型提供的程序集路径、反射类型或任意代码。

默认传输将持续事件流与控制请求放入独立连接池，避免旧 .NET Framework 的事件连接占满后无法发送控制请求。SDK 自管的事件池每个服务源最多使用 32 条连接，超过时按原请求超时等待；这不是会话或协议数量上限。注入 `HttpClient` 时仍沿用宿主的连接池与所有权，宿主须为持续事件流和控制请求安排足够容量，并保持禁重定向、禁共享 Cookie 等原约束。

业务工具、同轮输入、权限/提问、MCP、模型/思考、媒体和离线草稿直接参考三个[示例](../README.md#示例和开发)；完整实现与待验项不能只凭方法名判断。Electron 保留其原有完整 SDK、Node 与 IPC 模式。C# 的终端壳没有第二套 Agent Loop。

## 错误及恢复

| 情况 | 应用处理 |
|---|---|
| 401、撤权、旧身份/绑定 | 重新认证或沿可信恢复流程处理；不能给旧操作换身份后重发 |
| 版本/能力不支持 | 呈现能力边界；不要吞参数或降级存储 |
| 事件缺口 | 明确中断当前不完整投影，按允许的历史/恢复接口重建；不要把缺口检查关掉 |
| POST 已发送但响应丢失 | 保留原请求键、原输入、原目标和未知状态，查询/对账；不重做文件写入、Shell 或付费语音 |
| 容量不足、损坏、清理失败 | 保留可诊断失败；不把坏库当空库、不删除回执换空间、不报告已清理 |

只有协议明确允许的只读/原键恢复和重试错误才重试；应用不要给所有异常加一个通用重试循环。用量、当前上下文预算、供应商缓存命中及最终费用是不同数据，没有事实时保持未知。
