/**
 * preload — contextBridge 白名单:renderer 与主进程之间的唯一通道。
 *
 * 纪律(Electron 安全基线,demo 即样板):
 * - 恒不把 ipcRenderer 本体或任何含 Node 能力的对象暴露给页面;
 * - 逐通道具名封装——renderer 的全部可用面 = 下面这一个对象,再无其他;
 * - 通道名集中在此,与主进程 ipcMain 侧一一对应,改动一眼可查。
 */
import { contextBridge, ipcRenderer } from 'electron';

/** 只读订阅封装:renderer 传回调,恒拿不到 event 对象本体 */
function listen(channel: string): (callback: (payload: unknown) => void) => void {
  return (callback) => {
    ipcRenderer.on(channel, (_event, payload: unknown) => callback(payload));
  };
}

contextBridge.exposeInMainWorld('tansrChat', {
  // ———— 输入(renderer → 主进程)————
  /** 发送一条用户消息(主进程转 session.send) */
  send: (text: string) => ipcRenderer.send('chat:send', text),
  submitInput: (input: unknown) => ipcRenderer.invoke('chat:submit', input),
  pickImage: () => ipcRenderer.invoke('media:pick-image'),
  transcribeFile: () => ipcRenderer.invoke('media:audio', { action: 'transcribe-file' }),
  transcribeRecording: (audio: string) => ipcRenderer.invoke('media:audio', { action: 'transcribe', audio }),
  speak: (text: string) => ipcRenderer.invoke('media:audio', { action: 'speak', text }),
  planSpeech: (text: string, model: string | undefined, segment: boolean) => ipcRenderer.invoke('media:speech-plan', { text, model, segment }),
  speakSegment: (planId: string, index: number) => ipcRenderer.invoke('media:audio', { action: 'speak', planId, index }),
  speechStatus: (planId: string) => ipcRenderer.invoke('media:speech-status', planId),
  cancelAudio: () => ipcRenderer.send('media:cancel'),
  saveMedia: (id: string) => ipcRenderer.invoke('media:save', id),
  previewMedia: (id: string) => ipcRenderer.invoke('media:preview', id),
  /** 打断当前轮(主进程转 session.interrupt) */
  interrupt: () => ipcRenderer.send('chat:interrupt'),
  /** 答复权限确认框(allow=false 即拒绝) */
  answerPermission: (id: number, allow: boolean) =>
    ipcRenderer.send('permission:answer', { id, allow }),
  /** 答复提问对话框(AskUser 工具;answers = kernel Answer[] 同形) */
  answerQuestion: (id: number, answers: unknown[]) =>
    ipcRenderer.send('question:answer', { id, answers }),
  /** 切换对话模型(bundle 目录 handle;下一轮生效) */
  setModel: (handle: string) => ipcRenderer.send('chat:set-model', handle),
  /** 切换可注入持久化两态(开 = 落盘可恢复;关 = 纯内存并清卷) */
  setPersistence: (on: boolean) => ipcRenderer.send('chat:set-persistence', on),
  /** 切换视图呈现档(S-V1 delivery;块粒度即时生效,随时可切) */
  setDelivery: (payload: { text?: string; thinking?: string }) =>
    ipcRenderer.send('chat:set-delivery', payload),
  /** 切换思考生成(生成面旋钮 setThinking;下一轮生效,与呈现档正交) */
  setThinking: (on: boolean) => ipcRenderer.send('chat:set-thinking', on),
  /** 手动刷新终端自查用量(/v1/my-usage) */
  refreshUsage: () => ipcRenderer.send('chat:refresh-usage'),

  // ———— 订阅(主进程 → renderer)————
  /** SessionView 状态快照(每次变更全量推送) */
  onState: listen('chat:state'),
  /** 连接阶段:connecting / ready / fatal */
  onPhase: listen('chat:phase'),
  /** 能力面板数据(能力位/装配集/模型目录/技能/MCP/用量/持久化态) */
  onInfo: listen('chat:info'),
  /** Narrator 叙述行(层2 喷口;人类可读英文日志行) */
  onNarrator: listen('chat:narrator'),
  /** 持久化恢复的历史(纯文本投影;仅启动时可能出现) */
  onRestored: listen('chat:restored'),
  /** 主进程请求权限确认(弹框) */
  onPermissionAsk: listen('permission:ask'),
  /** 权限请求已被主进程收口(轮次中断等):关闭弹框 */
  onPermissionClosed: listen('permission:closed'),
  /** 智能体主动提问(AskUser 工具 → 提问对话框) */
  onQuestionAsk: listen('question:ask'),
  /** 提问已被主进程收口(轮次中断等):关闭对话框 */
  onQuestionClosed: listen('question:closed'),
});
