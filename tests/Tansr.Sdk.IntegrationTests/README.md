# Serve 源码 HTTP/SSE 联验

这里通过 C# SDK 调用真实 `packages/server/src/index.ts` 的 HTTP 路由、鉴权、会话注册表、SSE 编帧与重放日志。会话驱动明确采用原源仓 `FakeAgentFactory` 的受控子类，不调用模型、平台、付费采样或真实工具；结果不能作为完整 kernel/执行能力等价证明。

先由统一工程门编译 Release，再单独运行：

```powershell
$env:TANSR_SERVE_SOURCE = 'J:/tansr/tansr-cli'
node scripts/serve-integration.mjs
```

脚本硬性要求已安装依赖的干净 CLI 源码、源码导出与 tsx loader，记录当前 Git SHA 和两个入口文件 SHA256，拒绝旧 `dist`。它仅监听随机回环端口，用五分钟有效的随机合成令牌和内存数据；令牌不输出、不落文件。`dotnet test` 使用 `--no-build --no-restore` 与专用类过滤器。`TANSR_DOTNET` 可指定 dotnet 可执行文件，`TANSR_INTEGRATION_CONFIGURATION` 可显式选择已编译配置。

未设置必要环境直接失败，不静默跳过。独立测试只覆盖 SDK1 默认族的 create/send、真实在线非 ASCII SSE、取消观察不 interrupt、原游标重连、历史及分页、窗口缺口和身份隔离。重连后的历史来自当前受控会话，不能声称已证明跨进程持久恢复。所有端口和子进程均在退出时收口，工程卡与完整验收项仍须统一审阅。
