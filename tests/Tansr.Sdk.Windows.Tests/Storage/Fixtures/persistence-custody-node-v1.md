# Node 原目录消费夹具

`persistence-custody-node-v1.json` 是本批 Node SDK 真实加密 SQLite 导出的合成数据，SHA256：
`4c882304c771d4c1ea169f07b2c137a4cd57e4604aeb8f772aaaa65fc6e1d605`。

来源：PST-SERVE-01 的 `entry/node-import-net-custody.mts`；先消费 C# 原目录，冷重开并完成原 pending，
再创建 Node 原 pending、复用原 body-page，冻结新的原 operation 后导出。它含原业务根、两条永久
回执、三条历史传输和 Node 释放 reused 预留的原计量。所有内容为合成测试材料，无账号凭据。

这是 Windows 消费者私有的跨实现反例，不修改公共 schema、金样或验收阈值。对应测试通过已冻结
公共合同，以 5 项目录页和 4096 字节连续块导入，核原根、容量与票据，切权冷恢复后继续原 pending。
共同权威在本测试中受控；它不代替 SQL 权威或生产环境的验收。
