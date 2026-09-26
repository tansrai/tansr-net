# 原生示例的草稿、同轮插入与配置/记忆入口

WPF、WinForms 和 Console 共用公开 SDK。`inputs` 仍是原会话的同轮投递，不会取消、重建会话或截断文字。桌面“同轮插入草稿”与 Console `/insert <全文>`、`/insert-draft` 先读取当前 capabilities，再把原 `inputId`、`historyEpoch`、`turnId`、完整正文写入本机文件后发送。窗口草稿编辑期间收到较早发送的接纳回执，不会清除新编辑。

`/input-status`（窗口“查原插入回执”）查原目标；`/input-retry` 只在用户明确触发时重投原键、原正文和原目标。接纳是内存回执；`consumed` 只证明加入该轮历史，不证明模型已经收到/回答或内容已耐久落盘。原协议只保留当前与上一轮回执，`input_not_found` 不证明从未执行。未知投递较晚收到 `closed` 仍保留原未知事实，不自动换键开启新轮。未知输入未对账前不覆盖其上下文；如需另开独立工作，可显式指定另一份 `TANSR_EXAMPLE_STATE_FILE`，保留旧文件供以后对账。

三个示例默认把草稿和呈现保存到当前用户 `LocalApplicationData/Tansr/Examples/{wpf|winforms|console}.json`，可用 `TANSR_EXAMPLE_STATE_FILE` 改为应用管理的绝对路径。文件是明文本机应用数据，不含登录票据；应放在受限用户目录，不用于多用户共享。编辑与流呈现节流保存，成功发送、历史读取与正常关闭也保存；断电仍可能丢最后 750ms 的尚未保存编辑。单文件 32MiB 上限，超过时明确报错并保留内存文本，不裁剪后伪装完整。原插入在 POST 前同步写入并 flush。替换为同目录原子替换；文件指纹变化拒绝另一个实例的陈旧覆盖。保存失败会显示错误，窗口仍允许断开和退出。

桌面有“本机离线回看”及“恢复本机草稿”；Console `--offline` 不联网就能读取，交互可用 `/offline`、`/draft`、`/draft <全文>`、`/draft-file <UTF8文件>` 和 `/send-draft`。`/asr` 的文本也进入同一草稿。保存的会话 ID 不自动授权恢复；Console 仍需显式 `TANSR_RESUME_SESSION`。这些文件是当时的应用呈现与历史响应副本，可能已经过期或撤权，从不作为模型历史、材料源或 SQLite archive 当前授权。公开 `SqliteArchiveHistory` 需要宿主另装当前可信 authority；该共享档案离线能力仍是缺口。

## 提示词来源（只读）

桌面“提示词来源”与 Console `/prompt` 调用公开 `session.ReadApplicationPromptAsync()`，仅在显式读取时给原 metadata 请求增加 `?include=applicationPrompt`；不要求 terminal preview，也不改变原“状态”或 `/meta` 请求。结果表示 Serve 当前已经应用的应用提示词来源：`platform` 为平台应用设置，`sdk` 为开发者/Serve 可信宿主段，`platform+sdk` 为两者组合，`none` 为两段均未采用。它不是整个系统提示词为空的证明。

`fallback` 在开发者段未设置时采用平台段；`prepend` 将平台段放在开发者段之前。终端只观察来源与策略，不接收正文，也不能借此覆盖可信宿主配置。缺席、不支持、无效组合或会话不再 live 时显示“未知”，不能显示成 none 或假定默认策略。鉴权失败和网络错误仍明确报错，不转换成成功的未知响应。默认展示只用已校验的枚举；原始响应仅供应用按需诊断。

## 可选配置和记忆 preview

设置 `TANSR_TERMINAL_PREVIEW=1`，并把 `TANSR_TRUSTED_SCOPE_FILE` 指向宿主管理的身份配置，例如其结构为：

```json
{"principal":"应用可信身份键","scope":{"applicationScopeId":"应用标识","endUserId":"用户标识","authorizationRevision":"当前授权修订"}}
```

这些值必须由应用可信登录/授权管理取得；不能接受聊天输入、模型建议、未经验证 JWT 的解码内容，也不能从会话 metadata 自行推导。文件只是宿主声明，不是平台认证票据；每次请求最终仍由 Serve 认证与授权。SDK 每次读取文件，身份/修订变化会中止原操作，不能利用旧 scope 文件继续声称当前授权。未显式启用或未配置可信 scope 时不宣称已接通，重新连接后才启用。

窗口“配置 / 记忆（preview）”与 Console `/controls`、`/control <0..9> [参数]` 提供同一组动作：读取配置、修改模型、修改思考预算、显式重放原配置、读取记忆来源、remember 当前轮、pin 文字、forget 主题、查询原记忆操作、显式重放原记忆操作。`model` 使用实际受信别名；thinking 只支持原合同 `{budget:非负整数}` 或 `null`。提示词与其它预算治理没有被假装成已实现。

操作通过公开 `TerminalSessionControl`，合同 SHA 由 SDK 与 Serve 每次校验；它是显式 preview，未作为稳定协议发布。记忆 source、generation、revision 来自真实读响应；配置使用真实 expectedRevision。每次发送前原 Request/Scope 保存在草稿路径旁按 endpoint/session 隔离的 operation 文件中，pending/unknown 阻止生成替代键；重启恢复仍使用同一文件、原键与原 scope。日志明文，管理要求同上。回执显示 `executionComplete`、`durable`、`consumed` 的实际值；未知不当成功。

配置当前没有独立操作回执 GET，“读取当前配置”无法证明先前未知修改已提交；只能显式重放原请求。记忆提供原 operationId 查询。示例不自动重试副作用，不自动替换来源，也不实现另一套记忆决策内核。真实付费模型、记忆选材效果及各服务装配仍需按原 NET 验收单验证。
