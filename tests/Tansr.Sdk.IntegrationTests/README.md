# Serve 源码 HTTP/SSE 联验

这里使用两组回环监听，均导入 CLI 当前源码。SDK1 传输回归采用原 `FakeAgentFactory` 的受控子类；高阶链通过公开 `createServeArchiveHost`，使用真实平台装配、kernel 查询循环、设备执行、档案介质和 HTTP/SSE，仅把上游平台与模型替换为确定性合成响应。所有运行零真实平台凭据、零付费采样。

先由统一工程门编译 Release，再单独运行：

```powershell
$env:TANSR_SERVE_SOURCE = 'J:/tansr/tansr-cli'
node scripts/serve-integration.mjs
```

脚本硬性要求已安装依赖的干净 CLI 源码、源码导出与 tsx loader，记录当前 Git SHA、入口/夹具/schema SHA256，拒绝旧 `dist`。它仅监听随机回环端口，用五分钟有效的随机合成令牌；令牌不输出、不落文件。高阶链临时数据保留于忽略目录 `artifacts/serve-integration/run-*`，包括真实执行/档案 SQLite 和 DPAPI 加密密钥，便于失败查账。`dotnet test` 使用 `--no-build --no-restore` 与专用类过滤器。`TANSR_DOTNET` 可指定 dotnet 可执行文件，`TANSR_INTEGRATION_CONFIGURATION` 可显式选择已编译配置。

未设置必要环境直接失败，不静默跳过。SDK1 独立测试覆盖默认族的 create/send、真实在线非 ASCII SSE、取消观察不 interrupt、原游标重连、历史及分页、窗口缺口和身份隔离。高阶产品链覆盖公开 DeviceSessionHost 自动初始化/注册/绑定、原审批、Windows 真实 Read、原执行回执、真实轮终局、ArchiveClient 发现既有绑定、ArchiveTransferSession 拉页/正文、DPAPI/AES-GCM SQLite 耐久 ACK 和重开，再通过原档案 SSE 将真实材料请求交给 MaterialSource/SQLite 响应箱，上传后由原核心下一轮消费。材料请求/入队由受控文件 IPC 调用可信宿主生命周期；IPC 不传输材料正文、不代理 SSE，也不是新增产品 HTTP 路由。

测试会继续接收第二轮档案；如果该步暴露原修订冲突，保留原 ACK 并使联验失败，不能以延时、改键重投或绕开产品协调器遮掩。传输测试不能证明真实外部模型、跨进程 Serve 冷恢复或完整 SDK 行为等价。端口和子进程退出时收口，工程卡与完整验收项仍须统一审阅。
