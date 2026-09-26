import { resolveApplicationSystem } from '@tansr/sdk';
import { demoSystemFromEnv } from './system.js';
/**
 * 三档 API 进阶样例(无 GUI,纯 Node)—— createSession 之外的另外两档:
 *
 *   ① query:单轮便捷面。prompt 进、KernelEvent 流出,generator 的 return
 *     值是结构化终值(finalText / messages / usage / counters);
 *   ② runAgent:低阶薄包装。自带 client + model(这里经令牌档装配件
 *     assemblePlatformModel 取得),拿到内核 QueryHandle 自己控制事件泵
 *     ——适合已有自己装配层、只要查询环的场景。
 *
 * 聊天主流程请用 createSession(见 src/app.ts);本样例演示的是「不方便塞进
 * 聊天 UI 的两档」怎么单独用。
 *
 * 跑法(会真实调用两次模型,少量计费):
 *   npm run demo:advanced        # = node build.mjs && node dist/advanced-api.mjs
 * 令牌来源:环境变量 TANSR_APP_TOKEN 直给,或(缺省)向 token-server 示例
 * (TANSR_TOKEN_SERVER,缺省 127.0.0.1:8788)走演示登录换发。
 */
import { assemblePlatformModel, query, runAgent } from '@tansr/sdk';
import { fetchDemoToken } from './token.js';
import type { SdkCleanupHandle } from '@tansr/sdk';
import { closeDemoQuery, DemoCleanupIncompleteError, withDemoCleanup } from './lifecycle.js';

const TANSR_API_BASE = process.env.TANSR_API_BASE ?? 'http://127.0.0.1:8787';
const TOKEN_SERVER = process.env.TANSR_TOKEN_SERVER ?? 'http://127.0.0.1:8788';
// 即使观察失败也保留owner;应用可再次调用drain,不会重新执行原查询。
const pendingCleanup = new Set<SdkCleanupHandle>();

async function main(): Promise<void> {
  const token = process.env.TANSR_APP_TOKEN ?? (await fetchDemoToken(TOKEN_SERVER)).token;

  // ———— ① query:单轮一问一答,终值即结构化结果 ————
  console.log('[advanced] ① query(单轮便捷面)…');
  const run = query({
    token,
    baseUrl: TANSR_API_BASE,
    system: demoSystemFromEnv(process.env.TANSR_DEMO_SYSTEM),
    prompt: '用一句话说明「事件流 + 状态投影」这个 UI 架构的好处。',
    cleanupTimeoutMs: 30_000,
    onLifecycleError: (error) => {
      // 消费者自身抛错可能优先于AsyncIteratorClose错误;此回调保留第二条错误通道。
      console.error(`[advanced] query收尾未确认:${error.code};失败项=${error.failures.length}`);
      if (error.cleanup !== undefined) pendingCleanup.add(error.cleanup);
    },
  });
  let step = await run.next();
  while (!step.done) {
    if (step.value.type === 'msg.text.delta') process.stdout.write(step.value.text);
    step = await run.next();
  }
  const result = step.value;
  process.stdout.write('\n');
  const usageNote =
    result.usage !== undefined ? `in=${result.usage.inputTokens} out=${result.usage.outputTokens}` : '(无 usage 帧)';
  console.log(
    `[advanced] query 终值:reason=${result.reason} · usage ${usageNote} · messages=${result.messages.length}`,
  );

  // ———— ② runAgent:低阶句柄(自带 client/model,零装配糖) ————
  // 令牌档的 client 可经 assemblePlatformModel 取得(bundle 拉取 + registry
  // 轻装配 + 令牌头 fetch 包装,与 createSession 令牌档同一装配件)。
  console.log('[advanced] ② runAgent(低阶 QueryHandle)…');
  const assembled = await assemblePlatformModel({ token, baseUrl: TANSR_API_BASE });
  const handle = runAgent({
    // runAgent 零装配，宿主显式复用 SDK 策略解析器；不会自动取平台配置。
    system: resolveApplicationSystem(assembled, demoSystemFromEnv(process.env.TANSR_DEMO_SYSTEM)).system,
    client: assembled.client,
    model: assembled.model,
    prompt: '回答一个词:tansr 的 SDK 是 headless 还是带 UI?',
    maxTurns: 4,
  });
  let text = '';
  await withDemoCleanup(async () => {
    for await (const event of handle.events) {
      if (event.type === 'msg.text.delta') text += event.text;
    }
  }, () => closeDemoQuery(handle));
  console.log(`[advanced] runAgent 终文:${text.trim().slice(0, 120)}`);
  console.log(`[advanced] runAgent 历史快照:${handle.messages().length} 条消息(counters=${JSON.stringify(handle.counters())})`);
}

void withDemoCleanup(main, async () => {
  const results = await Promise.allSettled([...pendingCleanup].map(async (owner) => {
    const receipt = await owner.drain({ timeoutMs: 30_000 });
    if (receipt.status !== 'completed') throw new DemoCleanupIncompleteError(receipt);
    pendingCleanup.delete(owner);
  }));
  const failures = results.flatMap((result) => result.status === 'rejected' ? [result.reason] : []);
  if (failures.length > 0) throw new AggregateError(failures, 'query清理仍未完成,所有者引用已保留。');
}).then(() => {
  console.log('[advanced] PASS ✅ query 与 runAgent 两档及资源收尾均已走通');
}).catch((err: unknown) => {
  console.error('[advanced] FAIL:', err instanceof Error ? (err.stack ?? err.message) : String(err));
  process.exitCode = 1;
});
