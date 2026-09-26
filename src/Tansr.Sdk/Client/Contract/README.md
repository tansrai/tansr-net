# 档案事件事实源

`archive-events-sources.json` 锁定 `tansr-cli@027de7e2` 的原 `event-frame.ts`、`strict-event-stream.ts` 和 `event-connection.ts` 字节/哈希。它们解释原 `sdk2-ext-v1` 具名 `EventFrame` 的传输与关联语义，不修改根 `contract/` 冻结快照。

`TansrClient.ArchiveEvents` 仅负责真实 `/v3/sdk2/bindings/:id/events?protocol=sdk2-ext-v1` 单次连接。这个协议要求精确 `id: ` → `event: ` → 单行 `data: ` → 空行；严格 UTF-8、LF/CRLF、262,317 字节帧帽和 262,144 字节规范 JSON。不能复用支持多行 data、未知字段和普通数字 id 的会话 SSE 解码。

真实 cursor 为 `e1.k1.<43字符base64url>.<16字符epoch十六进制>.<16字符sequence十六进制>.<43字符MAC>`，eventId 必须与其绑定。端点只接收协议 query 与 `Last-Event-ID`；可信 generations 仅用来核对返回事件，不能作为客户端声称的授权。MAC 的真实性由 Serve 判定，客户端只检查格式与字段关联。

消费链分工：传输层验证规范 JSON、具名 schema、外层/头/cursor/绑定/generations，以及 payload 的 scope、target、revision 关联；`ArchiveClient.ConsumeEventsAsync` 在业务回调之前核查原档案/材料/绑定的 coverage、去重、容量等语义。回调应完成业务事实与 cursor 的耐久提交；成功返回后才推进该连接的已消费 cursor。失败/取消不推进，不自动重连，不自动 ACK，也不取消模型会话。宿主使用自己的耐久游标显式重连并处理重放幂等。

同一 `TansrClient` 每个binding最多一个档案消费者，总计最多8个在途槽。取消可以退出网络等待，但顽固业务回调仍占据原槽，直到原任务真实结束才释放；期间同binding重连返回本地 `consumer_pending`，总量用尽返回 `consumer_capacity_exceeded`，不会通过不断重连累积悬挂供材操作。此约束不影响独立会话SSE订阅，也不宣称强制终止宿主任意委托。

原 TS event-connection 使用通用 Id 形状检查传入 lastEventId，C# 消费者还检查它符合已有明确 cursor 格式，避免无效请求；此项不增加可接受的 wire 格式。
