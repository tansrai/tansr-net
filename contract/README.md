# .NET SDK 协议快照

事实源为 `tansr-cli/doc/rfc/sdk2-ext-v1.schema.json`。此目录按原字节锁定 schema、既有 wire 金样与参考编码器；`manifest.json` 记录来源提交、路径、字节数及 SHA256。引用材料不能作为第二套 wire 规范编辑。源仓其它文件有新提交不会单独令快照失效；改变协议文件必须先会签并重新核对消费者。

`scripts/check-contract.ps1` 核本地指纹；指定 `-SourceRoot J:/tansr/tansr-cli` 另核上游对应文件。脚本只检查，不更新、下载或改写任何文件。运行时具名校验使用嵌入的同一 schema；网络 JSON 不可提供 schema、程序集或类型名。公共指纹在 `WireContract`，公共方法接受 `JsonElement`，不依赖反射 DTO 序列化。

`sdk2-wire-v1.json` 原文件同时含规范控制字节正例、非规范反例及不可重编码的原 IR。`schema-vectors.json` 中前23个端点 DTO 正例机械提取原 `sdk2-extension-schema.test.mjs` 的 `goldens`；额外边界例仅用于验证 C# 消费，不定义新合同。具名校验不证明当前身份、持久 CAS、SHA 语义或副作用已授权；这些检查继续由会话、执行与存储层负责。

SDK1 的 JSON、默认持久化和未知可选字段规则不变；SDK2 扩展显式启用。`RFC-SERVE-NET-1` 的 `terminal-services-v1` 仍未冻结，本快照不定义、不实现其候选新路由。

本批只实现 NET-01 的协议消费部分，完整功能映射、真实 HTTP/平台/AOT 消费及 NET-A01—04 仍须由原父卡统一验收；未以快照检查关闭工程卡。
