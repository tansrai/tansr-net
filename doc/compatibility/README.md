# 能力与合同核对 / Capability and contract review

`public-api-map.json` 保留原 P01—P16 分组，将 TypeScript SDK 出口、AgentSession 成员、Electron 桥和能力表逐项对应到原生实现、Serve 前置与剩余条件。原 680 项中漏列的 `ChatApp.paths` 已补入，共 **681 项**；分组、工程卡和验收卡数量不变。

2026-09-27 核对的 Serve 主线为 `e63c4e709d2086f9e7343cb764814ecce130e3df`。原 11 份能力源文件与 `027de7e2` 快照逐字节相同，因此不移动 `WireContract.SourceRevision`、旧数据互通基准和原五份合同锁。`reviewedAgainst` 单独记录本轮实现审阅版本。

映射的 `evidence` 引用可定位的行为、源码入口、命令和回执。局部测试通过与完整分组通过分开：`complete` 只在全部原行为完成时为真；`ready-not-run` 不得用于关闭。检查器不会把方法名存在解释为真实行为通过，也不会再硬编码所有证据必须永远为空。

```powershell
node scripts/check-parity.mjs --source-root J:/tansr/tansr-cli
node --test scripts/check-parity.test.mjs
pwsh -File scripts/check-contract.ps1
```

检查会拒绝新出口未映射、删除困难成员的映射、源字节漂移、失效的实现/测试入口，以及使用未验材料关闭行为。成员枚举按锁定源码的声明语法运行，任何源码变化先失败，再审阅抽取器及映射。能力表是研发回填入口，不是完整 Electron 对等的自动证明。

## SDK1 / SDK2 发现与失败

默认 `TansrClient` 继续调用原 `/v2`，无需先访问 SDK2 发现接口。选择 `SessionContract.Sdk2OffloadV1` 时，客户端先验证当前身份和票据的能力，再访问 `/v3/sdk2`。404、401、网络错误、超时、损坏响应和缺能力均不会自动降级、重建会话或改换存储。

`GetSessionCapabilitiesAsync` 是显式刷新：刷新开始即撤销旧的发现缓存，并与自动发现共用锁。若同票下服务已经撤回 SDK2 能力，后续调用必须重新确认，不能沿用旧成功结果。新票据也需重新发现；旧请求晚到不能授权新票据。请求错误保持原协议码；取消/期限使用 .NET 的取消异常。宿主可以显式再次查询，但 SDK 不自动重做有副作用的调用。

`contract/session-compatibility.json` 是 Node 与 C# 共用的受控 HTTP 行为向量。Node 使用原项目的 `Sdk2SessionClient`，C# 使用公开 `TansrClient`，比较请求路由、次数、拒绝边界和不降级行为。原 Node 的 `capability_unconfirmed` 包装与 C# 的具体 HTTP/协议异常分别保留，未将两种错误外形强行改成同名。这些向量不是替代真实 Serve 联验。

```powershell
dotnet test tests/Tansr.Sdk.Tests/Tansr.Sdk.Tests.csproj -c Release --filter FullyQualifiedName~SessionCompatibilityTests
node --import file:///J:/tansr/tansr-cli/node_modules/tsx/dist/loader.mjs scripts/check-session-compatibility.mjs J:/tansr/tansr-cli
```

The map preserves all 16 capability groups and records implementation, evidence and remaining acceptance separately. The original wire lock remains unchanged. A successful inventory check is not full behavior acceptance.

SDK1 remains the default. SDK2 is explicit, authenticated and capability-gated. Failed discovery never changes the selected contract or storage strategy. Explicit refresh invalidates an earlier decision, and a renewed ticket requires fresh discovery. Side effects are not retried automatically. Shared vectors exercise the actual original Node client and the public .NET client with the same HTTP replies; language-specific error types remain explicit. Real Serve integration is a separate acceptance layer.
