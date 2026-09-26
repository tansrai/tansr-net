/**
 * renderer — 纯 UI 层:收「SessionView 状态快照 / 能力面板数据 / 叙述行」,
 * 发「用户输入 / 权限与提问答复 / 切模与设置」。
 *
 * 恒不手拼 KernelEvent:增量拼接、工具生命周期状态机、轮次边界、usage 累计、
 * 待办台账,全部由主进程里 SDK 的 SessionView 投影器完成;这里只是把状态
 * 画出来。全量重绘对 demo 体量绰绰有余;生产可按 message.id 做增量 DOM 或
 * 接任意框架(快照是普通不可变对象,天然适配 React/Vue 状态绑定)。
 *
 * 安全:模型输出与工具结果一律经 textContent 落 DOM(恒不 innerHTML),
 * 提示词注入拿不到脚本执行面;产图/产片内嵌只经 img.src / video.src 赋值
 * (URL 锚定 http(s),CSP img-src/media-src 再兜一层)。
 *
 * 媒体产物(doc/128):工具卡 part 上的 `artifact` 是主进程用 sdk `parseMediaArtifact`
 * 对 completed 卡 `result`(ToolResult.data)做的判别结果(`{ kind: 'image' | 'video' |
 * 'transcript' | 'speech', data }`,非媒体 / 错误回执为 null)——本文件只按 `artifact.kind`
 * 分派渲染,恒不再嗅探 `images` / `videos` / `audio.mime` 之类形状键(形状规则单源在
 * protocol,三端对表基准同一函数)。
 */
const api = window.tansrChat;

// 提示词、系统工具目录与媒体控制共用双语词表；未知语言沿示例缺省 zh-CN。
const PROMPT_MESSAGES = {
  'zh-CN': {
    'history.on': '已开启：聊天历史保存于 {path}；重新启动后恢复。',
    'history.off': '已关闭：当前会话保留在内存，退出后不自动恢复。',
    'image.remove': '移除图片', 'image.ready': '图片已加入草稿，可输入问题后发送。',
    'speech.weighted': '加权字符（汉字=2，其余码点=1）', 'speech.unicode_code_points': 'Unicode 码点', 'speech.utf16_code_units': 'UTF-16 码元', 'speech.utf8_bytes': 'UTF-8 字节',
    'speech.segment': '允许长文分段（最多 32000 加权字符 / 32 段）',
    'speech.progress': '正在生成片段', 'speech.stopped': '已停止，完成片段已保留；在途片段可能已计费。',
    'speech.resume': '继续尚未请求的片段', 'speech.partial': '部分片段结果不明，已跳过以避免重复计费。请核对原文和片段编号。',
    'speech.empty': '没有可朗读的正文，请输入文本或等待回复正文。', 'speech.message': '朗读',
    'image.busy': '本轮进行中，图文草稿已保留，请在本轮结束后发送。',
    'image.failed': '图片选择失败：',
    'context.title': '模型与上下文', 'context.unknown': '未知 / 当前 SDK 未提供',
    'context.selected': '下轮模型', 'context.active': '执行中模型', 'context.observed': '最近实测模型',
    'context.window': '物理 / 有效窗口', 'context.used': '估算输入 / 剩余',
    'context.reserve': '输出 / 思考预留', 'context.source': '设置来源', 'context.thresholds': '微压缩 / 警告 / 自动压缩 / 阻塞水位',
    'context.transition': '切模状态', 'context.note': '上下文是本地估算，区别于累计计费用量。缩小窗口先保留原文、生成摘要并验证目标预算；失败保留原模型。窗口及压缩策略由模型和 SDK 配置决定。',
    'prompt.title': '业务提示词',
    'prompt.pending': '等待会话装配',
    'prompt.platform': '来源：平台配置',
    'prompt.default': '来源：仅示例指南',
    'prompt.sdk': '来源：SDK 显式配置',
    'prompt.both': '来源：平台配置 + SDK 显式配置',
    'prompt.refresh': '平台业务提示词和策略会在下一轮开始前自动读取；执行中的轮次保持当前配置，会话历史和工具、媒体使用指南继续保留。',
    'tools.title': '系统工具',
    'tools.note': '本地、网络、媒体共用系统工具机制。启用状态来自本会话实际装配，调用仍须遵守权限与平台授权。',
    'tools.details': '开发者详情：授权字段',
    'tools.contractNote': 'platform.* 是兼容保留的授权字段，不代表另一类工具。媒体与平台联网搜索使用平台提供方及对应额度；读取网页和 HTTP 请求由宿主出站客户端访问目标站点。',
    'tools.local': '本地与任务', 'tools.network': '网络', 'tools.media': '媒体',
    'tools.enabled': '已装配', 'tools.disabled': '未装配', 'tools.unconfigured': '待配置模型',
    'tools.unavailableNote': '未装配此工具，请检查应用授权后重新连接；技术字段见开发者详情。',
    'tools.custom': '业务工具', 'tools.task': '子代理', 'tools.skill': '技能', 'tools.mcp': 'MCP 服务',
    'tools.attached': '已附着', 'tools.unattached': '未附着',
    'tools.read': '读取文件', 'tools.write': '写入文件', 'tools.edit': '编辑文件', 'tools.glob': '查找文件',
    'tools.grep': '搜索内容', 'tools.list': '列出目录', 'tools.shell': '执行命令', 'tools.todoWrite': '管理待办', 'tools.askUser': '询问用户',
    'tools.webFetch': '读取网页', 'tools.webSearch': '联网搜索', 'tools.http': 'HTTP 请求',
    'tools.imageGen': '生成图片', 'tools.videoGen': '生成视频', 'tools.speechToText': '语音转文字', 'tools.textToSpeech': '文字转语音',
    'media.directTitle': '用户音频操作',
    'media.directNote': '转写先填入可编辑草稿，不会自动发送给智能体；朗读直接生成音频，与模型工具调用分开显示。',
    'media.pickImage': '选择图片', 'media.transcribe': '音频转写', 'media.record': '录音转写', 'media.stopRecording': '停止录音',
    'media.speak': '朗读输入', 'media.cancel': '取消音频请求', 'media.models': '媒体可用模型',
    'media.processing': '音频处理中…', 'media.cancelled': '已取消选择',
    'media.draft': '转写已填入输入框，请检查后发送', 'media.generated': '语音已生成，点击播放器播放',
    'media.failed': '音频请求失败：',
  },
  en: {
    'history.on': 'Enabled: History is saved to {path} and restored on restart.',
    'history.off': 'Disabled: The current session stays in memory and will not resume after exit.',
    'image.remove': 'Remove image', 'image.ready': 'Image added to the draft. Add a question and send.',
    'speech.weighted': 'weighted characters (Han=2, other code points=1)', 'speech.unicode_code_points': 'Unicode code points', 'speech.utf16_code_units': 'UTF-16 code units', 'speech.utf8_bytes': 'UTF-8 bytes',
    'speech.segment': 'Allow long-text segments (up to 32000 weighted characters / 32 segments)',
    'speech.progress': 'Generating segment', 'speech.stopped': 'Stopped. Completed segments are retained; an in-flight segment may have been billed.',
    'speech.resume': 'Continue unrequested segments', 'speech.partial': 'Some segment results are unknown and were skipped to avoid duplicate billing. Check the original text and segment numbers.',
    'speech.empty': 'There is no text to read aloud. Enter text or wait for the response.', 'speech.message': 'Read aloud',
    'image.busy': 'A turn is running. The image and text draft are retained for the next turn.',
    'image.failed': 'Image selection failed: ',
    'context.title': 'Model and context', 'context.unknown': 'Unknown / unavailable in this SDK',
    'context.selected': 'Next model', 'context.active': 'Active model', 'context.observed': 'Last observed model',
    'context.window': 'Physical / effective window', 'context.used': 'Estimated input / remaining',
    'context.reserve': 'Output / thinking reserve', 'context.source': 'Settings source', 'context.thresholds': 'Micro / warning / auto / blocking thresholds',
    'context.transition': 'Model transition', 'context.note': 'Context is a local estimate, separate from cumulative billed usage. A smaller window requires a source backup, summary and target budget check; failure retains the original model. The model and SDK configuration determine window and compaction settings.',
    'prompt.title': 'Application prompt',
    'prompt.pending': 'Waiting for session setup',
    'prompt.platform': 'Source: Platform configuration',
    'prompt.default': 'Source: Demo guidance only',
    'prompt.sdk': 'Source: Explicit SDK configuration',
    'prompt.both': 'Source: Platform + explicit SDK configuration',
    'prompt.refresh': 'The platform application prompt and policy are read automatically before the next turn. An active turn keeps its current configuration; conversation history and tool and media guidance are preserved.',
    'tools.title': 'System tools',
    'tools.note': 'Local, network, and media tools share the system tool mechanism. Availability reflects this session’s assembly; each call remains subject to permissions and platform authorization.',
    'tools.details': 'Developer details: Authorization fields',
    'tools.contractNote': 'platform.* contains compatibility authorization fields, not a separate tool class. Media and platform web search use platform providers and their quotas; WebFetch and Http reach target sites through the host egress client.',
    'tools.local': 'Local and tasks', 'tools.network': 'Network', 'tools.media': 'Media',
    'tools.enabled': 'Assembled', 'tools.disabled': 'Not assembled', 'tools.unconfigured': 'Model configuration needed',
    'tools.unavailableNote': 'This tool is not assembled. Check application authorization and reconnect; field names are in Developer details.',
    'tools.custom': 'Business tools', 'tools.task': 'Subagents', 'tools.skill': 'Skills', 'tools.mcp': 'MCP services',
    'tools.attached': 'Attached', 'tools.unattached': 'Not attached',
    'tools.read': 'Read files', 'tools.write': 'Write files', 'tools.edit': 'Edit files', 'tools.glob': 'Find files',
    'tools.grep': 'Search contents', 'tools.list': 'List directories', 'tools.shell': 'Run commands', 'tools.todoWrite': 'Manage tasks', 'tools.askUser': 'Ask the user',
    'tools.webFetch': 'Read webpages', 'tools.webSearch': 'Web search', 'tools.http': 'HTTP requests',
    'tools.imageGen': 'Generate images', 'tools.videoGen': 'Generate videos', 'tools.speechToText': 'Speech to text', 'tools.textToSpeech': 'Text to speech',
    'media.directTitle': 'User audio actions',
    'media.directNote': 'Transcription fills an editable draft and is not sent to the agent automatically. Read-aloud generates audio directly, separately from agent tool calls.',
    'media.pickImage': 'Choose image', 'media.transcribe': 'Transcribe audio', 'media.record': 'Record audio', 'media.stopRecording': 'Stop recording',
    'media.speak': 'Read input aloud', 'media.cancel': 'Cancel audio request', 'media.models': 'Available media models',
    'media.processing': 'Processing audio…', 'media.cancelled': 'Selection cancelled',
    'media.draft': 'Transcription added to the draft. Review it before sending.', 'media.generated': 'Audio is ready. Use the player to listen.',
    'media.failed': 'Audio request failed: ',
  },
};

function t(key) {
  const lang = document.documentElement.lang.toLowerCase().startsWith('en') ? 'en' : 'zh-CN';
  return PROMPT_MESSAGES[lang][key];
}
const mediaStatusEl = document.getElementById('media-status');
const directMediaEl = document.getElementById('direct-media');
const pickImageEl = document.getElementById('pick-image');
const transcribeFileEl = document.getElementById('transcribe-file');
const recordAudioEl = document.getElementById('record-audio');
const speakInputEl = document.getElementById('speak-input');
const cancelAudioEl = document.getElementById('cancel-audio');
let connected = false;
let audioBusy = false;
let recorder = null;
let recordingTimer = null;
let recordingCancelled = false;
let imageDraft = null;
let pickingImage = false;
let speechCancelled = false;
let speechPlan = null;
let speechEpoch = 0;
const completedMediaCards = new Map();

function canSpeak() {
  return connected && info !== null && !audioBusy && recorder === null && info.capabilities.platform.textToSpeech;
}

function updateMediaControls() {
  const usable = connected && info !== null && !audioBusy;
  pickImageEl.disabled = !usable || pickingImage || !info.imageInput;
  transcribeFileEl.disabled = !usable || recorder !== null || !info.capabilities.platform.speechToText;
  recordAudioEl.disabled = !usable || !info.capabilities.platform.speechToText;
  speakInputEl.disabled = !canSpeak();
  document.getElementById('resume-speech').disabled = !canSpeak();
  for (const button of document.querySelectorAll('.speak-message')) button.disabled = !canSpeak();
  cancelAudioEl.hidden = !audioBusy && recorder === null;
}

function appendSaveButtons(parent, files) {
  for (const file of files ?? []) {
    const button = el('button', 'media-save', `保存 ${file.name}`);
    button.type = 'button';
    button.addEventListener('click', async () => {
      button.disabled = true;
      try { const saved = await api.saveMedia(file.id); mediaStatusEl.textContent = saved ? `已保存：${saved}` : '已取消保存'; }
      catch (error) { mediaStatusEl.textContent = `保存失败：${error.message}`; }
      finally { button.disabled = false; }
    });
    parent.append(button);
  }
}

async function runAudio(call) {
  if (audioBusy) return;
  audioBusy = true; updateMediaControls(); mediaStatusEl.textContent = t('media.processing');
  try {
    const result = await call();
    if (!result) { mediaStatusEl.textContent = t('media.cancelled'); return; }
    directMediaEl.replaceChildren();
    renderMediaArtifact(directMediaEl, result.artifact, result.mediaFiles); appendSaveButtons(directMediaEl, result.mediaFiles);
    if (result.artifact.kind === 'transcript') {
      // 转写先填草稿，用户可编辑；不伪造用户音频 IR，也不自动触发模型请求。
      const text = result.artifact.data.text;
      inputEl.value = [inputEl.value, text].filter(Boolean).join('\n'); inputEl.focus();
      mediaStatusEl.textContent = t('media.draft');
    } else mediaStatusEl.textContent = t('media.generated');
  } catch (error) { mediaStatusEl.textContent = t('media.failed') + error.message; }
  finally { audioBusy = false; updateMediaControls(); }
}

async function runSpeech(text, resume = false) {
  if (!canSpeak()) return;
  const resumeButton = document.getElementById('resume-speech');
  resumeButton.hidden = true;
  if (!resume) {
    // 新请求取得批次所有权，失败也不能误续上一次的原文。
    speechPlan = null;
    if (typeof text !== 'string' || !text.trim()) { mediaStatusEl.textContent = t('speech.empty'); return; }
  }
  const epoch = speechEpoch;
  const isCurrent = () => epoch === speechEpoch;
  audioBusy = true; speechCancelled = false; updateMediaControls();
  try {
    const plan = resume ? speechPlan : await api.planSpeech(text, document.getElementById('speech-model').value || undefined, document.getElementById('speech-segment').checked);
    if (!isCurrent() || !plan) return;
    speechPlan = plan;
    if (speechCancelled) return;
    if (!resume) directMediaEl.replaceChildren();
    const status = await api.speechStatus(plan.id);
    for (let index = 0; index < plan.segments; index++) {
      if (!isCurrent() || speechCancelled) return;
      if (status.states[index] !== 'ready') continue;
      mediaStatusEl.textContent = `${t('speech.progress')} ${index + 1}/${plan.segments} · ${plan.model} · ${plan.estimatedCharacters}`;
      const result = await api.speakSegment(plan.id, index);
      if (!isCurrent() || speechCancelled) return;
      const card = el('div','direct-speech-segment');
      card.append(el('div','',`${index + 1}/${plan.segments}`));
      renderMediaArtifact(card, result.artifact, result.mediaFiles); appendSaveButtons(card, result.mediaFiles); directMediaEl.append(card);
    }
    const finished = await api.speechStatus(plan.id);
    if (!isCurrent()) return;
    mediaStatusEl.textContent = speechCancelled ? t('speech.stopped') : finished.states.some(state => state === 'uncertain') ? t('speech.partial') : t('media.generated');
  } catch (error) { if (isCurrent()) mediaStatusEl.textContent = (speechCancelled ? t('speech.stopped') : t('media.failed') + error.message); }
  finally {
    if (isCurrent() && speechPlan) {
      const plan = speechPlan;
      try {
        const status = await api.speechStatus(plan.id);
        if (isCurrent() && speechPlan === plan) {
          resumeButton.hidden = !status.states.includes('ready');
          if (speechCancelled) mediaStatusEl.textContent = t('speech.stopped');
        }
      } catch { if (isCurrent()) { speechPlan = null; resumeButton.hidden = true; } }
    }
    audioBusy = false; updateMediaControls();
  }
}
document.getElementById('resume-speech').addEventListener('click', () => void runSpeech('', true));

const statusEl = document.getElementById('status');
const phaseEl = document.getElementById('phase');
const transcriptEl = document.getElementById('transcript');
const placeholderEl = document.getElementById('placeholder');
const restoredEl = document.getElementById('restored-section');
const usageEl = document.getElementById('usage');
const errorEl = document.getElementById('error');
const composerEl = document.getElementById('composer');
const inputEl = document.getElementById('input');
const sendEl = document.getElementById('send');
const stopEl = document.getElementById('stop');
const chipsEl = document.getElementById('chips');
const modelSelectEl = document.getElementById('model-select');
const capsGridEl = document.getElementById('caps-grid');
const capsNoteEl = document.getElementById('caps-note');
const assemblyListEl = document.getElementById('assembly-list');
const systemPromptTitleEl = document.getElementById('system-prompt-title');
const systemPromptSourceEl = document.getElementById('system-prompt-source');
const systemPromptNoteEl = document.getElementById('system-prompt-note');
const mediaListEl = document.getElementById('media-list');
const usagePanelEl = document.getElementById('usage-panel');
const quotaNoteEl = document.getElementById('quota-note');
const usageRefreshEl = document.getElementById('usage-refresh');
const skillsListEl = document.getElementById('skills-list');
const mcpListEl = document.getElementById('mcp-list');
const todoListEl = document.getElementById('todo-list');
const narratorLogEl = document.getElementById('narrator-log');
const persistenceToggleEl = document.getElementById('persistence-toggle');
const persistenceNoteEl = document.getElementById('persistence-note');
const deliveryTextEl = document.getElementById('delivery-text');
const deliveryThinkingEl = document.getElementById('delivery-thinking');
const deliveryNoteEl = document.getElementById('delivery-note');
const thinkingToggleEl = document.getElementById('thinking-toggle');
const thinkingNoteEl = document.getElementById('thinking-note');
const permBackdropEl = document.getElementById('permission-backdrop');
const permNameEl = document.getElementById('perm-name');
const permArgsEl = document.getElementById('perm-args');
const permAllowEl = document.getElementById('perm-allow');
const permDenyEl = document.getElementById('perm-deny');
const questionBackdropEl = document.getElementById('question-backdrop');
const questionBodyEl = document.getElementById('q-body');
const questionSubmitEl = document.getElementById('q-submit');

/** SessionView 状态词表 → 中文(doc/84 §5.6 层1 status) */
const STATUS_LABEL = {
  idle: '空闲',
  thinking: '思考中',
  responding: '回复中',
  tooling: '调用工具',
  awaiting_permission: '等待授权',
  compacting: '压缩上下文',
  error: '出错',
};

/** 工具调用 part 状态 → 中文(全生命周期原位更新) */
const TOOL_STATUS_LABEL = {
  proposed: '待执行',
  awaiting_permission: '等待授权',
  running: '执行中…',
  completed: '已完成',
  failed: '失败',
  denied: '已拒绝',
  aborted: '已中止',
};

const TODO_STATUS_LABEL = {
  pending: '待办',
  in_progress: '进行中',
  completed: '已完成',
  cancelled: '已取消',
};

const BUSY_STATUSES = new Set(['thinking', 'responding', 'tooling', 'awaiting_permission', 'compacting']);

/** 最近一次能力面板数据(chat:info;chips 可用性与面板渲染的事实源) */
let info = null;
let hasRestored = false;
let hasMessages = false;

// ———————————————————————— 通用 DOM 助手 ————————————————————————

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function compactJson(value, maxChars) {
  let text;
  try {
    text = JSON.stringify(value, null, 2) ?? 'null';
  } catch {
    text = String(value);
  }
  return text.length > maxChars ? `${text.slice(0, maxChars)}…` : text;
}

// —— 内嵌图/内嵌视频公共件 ——
// 恒经 src 赋值落 DOM(与 textContent 同纪律,恒不 innerHTML 拼接)。加载
// 失败(断链/过期/上游未启)换成可见兜底块——0 尺寸空白「看着像失败」,
// 用户无从分辨是没画出来还是没显示出来(2026-08-29 1×1 PNG 事故同款纪律)。
function appendImage(parent, url, alt, className) {
  if (!/^(https?:\/\/|data:image\/(?:png|jpeg|webp);base64,)/i.test(url)) { parent.append(el('div', 'image-fallback', '图片源不受支持')); return; }
  const img = el('img', className);
  img.alt = alt;
  img.addEventListener(
    'error',
    () => {
      img.replaceWith(el('div', 'image-fallback', `图片加载失败:${url}(链接可能已失效,可复制到浏览器重试)`));
    },
    { once: true },
  );
  img.src = url;
  parent.append(img);
}

function appendVideo(parent, url, className) {
  if (!/^(https?:\/\/|data:video\/(?:mp4|webm);base64,)/i.test(url)) { parent.append(el('div', 'image-fallback', '视频源不受支持')); return; }
  const video = el('video', className);
  video.controls = true;
  video.preload = 'metadata';
  video.addEventListener(
    'error',
    () => {
      video.replaceWith(el('div', 'image-fallback', `视频加载失败:${url}(链接可能已失效,可复制到浏览器重试)`));
    },
    { once: true },
  );
  video.src = url;
  parent.append(video);
}

// doc/123 TextToSpeech 产物(SpeechData.audio):URL 托管形直播;b64 内联形拼 data: URI
// 播放(CSP media-src 已含 data:)。同 img/video 纪律:src 赋值、失败可见兜底。
// 两源皆缺(如 cli 径只落盘 path)→ 可见兜底,恒不留哑播放器。
function appendAudio(parent, audio, className, localFile) {
  if (audio.path && !audio.url && !audio.b64 && localFile) {
    const placeholder = el('div', 'image-fallback', '正在读取宿主音频产物…'); parent.append(placeholder);
    api.previewMedia(localFile.id).then((src) => {
      placeholder.remove(); appendAudio(parent, { url: src }, className);
    }, (error) => { placeholder.textContent = `本地音频预览失败：${error.message}`; });
    return;
  }
  if (typeof audio.url !== 'string' && typeof audio.b64 !== 'string') {
    parent.append(el('div', 'image-fallback', '音频产物无可播放源(既无 url 也无内联 b64)'));
    return;
  }
  const src = typeof audio.url === 'string' ? audio.url : `data:${audio.mime};base64,${audio.b64}`;
  if (!/^(https?:\/\/|data:audio\/[\w.+-]+;base64,)/i.test(src)) { parent.append(el('div', 'image-fallback', '音频源不受支持')); return; }
  const player = el('audio', className);
  player.controls = true;
  player.preload = 'metadata';
  player.addEventListener(
    'error',
    () => {
      player.replaceWith(
        el('div', 'image-fallback', typeof audio.url === 'string' ? `音频加载失败:${audio.url}(链接可能已失效,可复制到浏览器重试)` : '音频解码失败(内联 base64 形)'),
      );
    },
    { once: true },
  );
  player.src = src;
  parent.append(player);
}

// 文本气泡里的 markdown 图片 ![alt](http…) 渲染为真 <img>:模型常把产图
// 链接以 markdown 形态复述,不渲染就是一行裸文本。恒不引入 markdown 引擎:
// 只认图片这一种语法且 URL 锚定 http(s),其余文本原样按文本节点落 DOM。
const MD_IMAGE_RE = /!\[([^\]\n]*)\]\((https?:\/\/[^\s)]+)\)/g;

function renderTextBubble(text) {
  const bubble = el('div', 'bubble');
  let cursor = 0;
  for (const match of text.matchAll(MD_IMAGE_RE)) {
    if (match.index > cursor) bubble.append(text.slice(cursor, match.index)); // 字符串 append = 纯文本节点
    appendImage(bubble, match[2], match[1] === '' ? '图片' : match[1], 'bubble-image');
    cursor = match.index + match[0].length;
  }
  if (cursor < text.length) bubble.append(text.slice(cursor));
  return bubble;
}

// ———————————————————————— 会话区渲染 ————————————————————————

function renderToolPart(part, activeTools, cacheKey) {
  if (part.status === 'completed' && part.artifact && completedMediaCards.has(cacheKey)) return completedMediaCards.get(cacheKey);
  const card = el('div', 'tool');
  const head = el('div', 'tool-head');
  head.append(el('span', 'tool-name', part.name));
  const statusClass = part.status === 'failed' || part.status === 'denied' ? ` ${part.status}` : '';
  head.append(el('span', `tool-status${statusClass}`, TOOL_STATUS_LABEL[part.status] ?? part.status));
  if (typeof part.durationMs === 'number') {
    head.append(el('span', 'tool-status', `${(part.durationMs / 1000).toFixed(1)}s`));
  }
  card.append(head);

  if (part.args !== undefined) {
    const args = el('pre');
    args.textContent = compactJson(part.args, 400);
    card.append(args);
  }
  // 进行中:活跃条目上有进度消息与输出尾部(终态后自然消失,结果看下方)
  const active = activeTools.find((tool) => tool.id === part.id);
  if (active !== undefined && (active.progressMessage || active.outputTail)) {
    card.append(el('pre', '', [active.progressMessage, active.outputTail].filter(Boolean).join('\n')));
  }
  if (part.status === 'completed') {
    // 完成卡:part.result 是 ToolResult.data 的透传位(SessionView 投影原样携带)。
    // 媒体四工具(ImageGen / VideoGen / SpeechToText / TextToSpeech)的产物按主进程下发的
    // artifact.kind 分派(判别单源 parseMediaArtifact,见文件头);WebSearch(S-WS2 环2
    // 工具骑平台后端)携 results[](title/url/snippet)——结构化结果卡。URL 约 24h 失效,
    // 同时给出文本便于复制转存;恒不用 <a>(桌面窗口内导航是安全反模式),要打开就
    // 复制到浏览器。
    const ws = part.name === 'WebSearch' ? part.result : undefined;
    if (part.artifact !== null && part.artifact !== undefined && renderMediaArtifact(card, part.artifact, part.mediaFiles)) {
      appendSaveButtons(card, part.mediaFiles);
      completedMediaCards.set(cacheKey, card);
    } else if (ws && Array.isArray(ws.results) && ws.results.length > 0) {
      // 搜索结果卡:三段全经 textContent 落 DOM(el 助手同纪律)——title/snippet
      // 来自外部网页属不受信输入,恒不 innerHTML;URL 以纯文本呈现(复制到浏览
      // 器打开),链接导航面恒不进窗口。
      const list = el('div', 'search-results');
      for (const item of ws.results) {
        if (typeof item?.url !== 'string') continue;
        const row = el('div', 'search-result');
        row.append(el('div', 'search-title', typeof item.title === 'string' && item.title !== '' ? item.title : '(无标题)'));
        row.append(el('div', 'search-url', item.url));
        if (typeof item.snippet === 'string' && item.snippet !== '') {
          row.append(el('div', 'search-snippet', item.snippet));
        }
        list.append(row);
      }
      card.append(list);
      card.append(el('div', 'gen-image-url', `共 ${ws.results.length} 条结果(按次计费;链接可复制到浏览器打开)`));
    } else {
      const summary = part.resultText ?? (part.result !== undefined ? compactJson(part.result, 400) : '');
      if (summary !== '') card.append(el('pre', '', summary.length > 400 ? `${summary.slice(0, 400)}…` : summary));
    }
  }
  if (part.status === 'failed') {
    card.append(el('pre', '', `${part.errorType ?? 'error'}: ${part.errorMessage ?? ''}`));
  }
  return card;
}

/**
 * 媒体产物四形渲染(kind 由主进程 parseMediaArtifact 判别,本函数只按 kind 分派、不判形):
 *   image      → 逐张 <img> 真解码 + 可复制 URL 行(断链落可见兜底)
 *   video      → 逐条 <video> + URL 行(附计费秒数)
 *   transcript → 转写文本 <pre>(textContent;有说话人分离时逐段列出)+ 模型/语种/时长元信息行
 *   speech     → <audio>(URL 直播 / 内联 b64 拼 data: URI)+ 格式/时长/计费字符行
 * 返回 false = 该产物无可渲染内容(如空图集),调用方回落文本摘要。
 */
function renderMediaArtifact(card, artifact, files) {
  const data = artifact.data;
  switch (artifact.kind) {
    case 'image': {
      const images = data.images.filter((image) => typeof image?.url === 'string');
      if (images.length === 0) return false;
      for (const image of images) {
        appendImage(card, image.url, '生成的图片', 'gen-image'); // CSP 仅对图片放宽外链(见 index.html 注释)
        card.append(el('div', 'gen-image-url', image.url.startsWith('data:') ? '内联图片' : `${image.url}(链接可能过期，可保存到本地)`));
      }
      return true;
    }
    case 'video': {
      const videos = data.videos.filter((video) => typeof video?.url === 'string');
      if (videos.length === 0) return false;
      for (const video of videos) {
        appendVideo(card, video.url, 'gen-video'); // CSP media-src 放宽同 img 例
        card.append(
          el(
            'div',
            'gen-image-url',
            `${video.url}(约 24h 失效,及时转存;计费秒数 ${typeof data.billedSeconds === 'number' ? data.billedSeconds : '?'}s)`,
          ),
        );
      }
      return true;
    }
    case 'transcript': {
      const transcript = el('div', 'transcript');
      const meta = [
        `模型 ${data.model}`,
        typeof data.language === 'string' ? `语种 ${data.language}` : null,
        typeof data.durationSec === 'number' ? `时长 ${data.durationSec}s(按音频秒/token 计费)` : null,
      ].filter(Boolean);
      transcript.append(el('div', 'gen-image-url', meta.join(' · ')));
      const body = el('pre', 'transcript-text');
      body.textContent = data.text === '' ? '(空转写)' : data.text; // 转写文本 = 不受信输入,恒 textContent
      transcript.append(body);
      if (Array.isArray(data.segments) && data.segments.some((s) => typeof s?.speaker === 'string')) {
        for (const seg of data.segments) {
          if (typeof seg?.text !== 'string') continue;
          transcript.append(el('div', 'search-snippet', `[${seg.speaker ?? '?'} ${seg.start}s–${seg.end}s] ${seg.text}`));
        }
      }
      card.append(transcript);
      return true;
    }
    case 'speech': {
      const audio = data.audio;
      appendAudio(card, audio, 'gen-audio', files?.[0]);
      const format = typeof audio.format === 'string' ? audio.format : audio.mime;
      const duration = typeof audio.durationMs === 'number' ? `,时长 ${(audio.durationMs / 1000).toFixed(1)}s` : '';
      const billed = `计费字符 ${typeof data.billedChars === 'number' ? data.billedChars : '?'}`;
      card.append(
        el(
          'div',
          'gen-image-url',
          typeof audio.url === 'string'
            ? `${audio.url}(约 24h 失效,及时转存;${format}${duration};${billed})`
            : `内联音频(${format}${duration};${billed})`,
        ),
      );
      return true;
    }
    default:
      return false;
  }
}

/** 思考折叠块的用户开合记忆(key = messageId:partIndex;全量重绘恒不冲用户意图) */
const thinkingOpenChoice = new Map();

function renderMessage(message, activeTools, live) {
  const wrap = el('div', `message ${message.role}`);
  for (const src of live?.images ?? []) appendImage(wrap, src, '', 'gen-image');
  for (const [i, part] of message.parts.entries()) {
    if (part.type === 'text') {
      wrap.append(renderTextBubble(part.text));
    } else if (part.type === 'thinking') {
      // 流式可视(2026-09-01「思考没有流式输出」定谳:内容一直在流,但默认
      // 折叠 + 每帧全量重绘重置开合态,肉眼恒不可见):思考进行中自动展开
      // (边想边看),定稿自动收起;用户手动开合恒优先(记忆跨重绘)。
      const key = `${message.id}:${i}`;
      const isStreamingThinking =
        live !== undefined &&
        live.isLast &&
        live.status === 'thinking' &&
        i === message.parts.length - 1;
      const details = el('details', 'thinking');
      const chosen = thinkingOpenChoice.get(key);
      details.open = chosen !== undefined ? chosen : isStreamingThinking;
      const summary = el('summary', '', '思考过程');
      summary.addEventListener('click', () => {
        // click 先于原生 toggle:记录用户意图态(= 取反当前开合)
        thinkingOpenChoice.set(key, !details.open);
      });
      details.append(summary);
      const pre = el('pre');
      pre.textContent = part.text;
      details.append(pre);
      wrap.append(details);
    } else if (part.type === 'toolCall') {
      wrap.append(renderToolPart(part, activeTools, `${message.id}:${i}:${part.id}`));
    }
  }
  if (message.role === 'assistant') {
    const text = message.parts.filter((part) => part.type === 'text').map((part) => part.text).join('\n');
    if (text.trim() && info?.capabilities.platform.textToSpeech) {
      const speak = el('button', 'speak-message', t('speech.message'));
      speak.disabled = !canSpeak();
      speak.type = 'button'; speak.addEventListener('click', () => runSpeech(text));
      wrap.append(speak);
    }
  }
  return wrap;
}

function renderTodos(todos) {
  for (const stale of todoListEl.querySelectorAll('li')) stale.remove();
  if (todos.length === 0) {
    todoListEl.append(el('li', 'panel-empty', '暂无待办。'));
    return;
  }
  for (const todo of todos) {
    const li = el('li', `todo-item ${todo.status}`);
    li.append(el('span', 'todo-badge', TODO_STATUS_LABEL[todo.status] ?? todo.status));
    li.append(el('span', 'todo-text', todo.content));
    todoListEl.append(li);
  }
}

let inputTarget = null;
let inputTurnBusy = false;
const inputReceiptEl = document.createElement('div');
inputReceiptEl.setAttribute('role', 'status');
composerEl.after(inputReceiptEl);
const INPUT_STATE_LABEL = { reserved: '待接纳', accepted: '已接纳（内存）', consumed: '已并入本轮上下文', closed: '本轮结束，未处理', cancelled: '已取消' };

function render(state) {
  inputTarget = state.inputTarget ?? null;
  inputTurnBusy = BUSY_STATUSES.has(state.status);
  sendEl.textContent = inputTurnBusy ? '补充说明' : '发送';
  const receipt = state.inputReceipts?.at(-1);
  if (receipt) inputReceiptEl.textContent = INPUT_STATE_LABEL[receipt.state] ?? receipt.state;
  // 状态徽章
  statusEl.textContent = STATUS_LABEL[state.status] ?? state.status;
  statusEl.className = `status ${state.status === 'error' ? 'error' : BUSY_STATUSES.has(state.status) ? 'busy' : 'idle'}`;
  stopEl.hidden = !BUSY_STATUSES.has(state.status);

  // 逐请求 usage:SessionView.state.usage 直接绑定(cost.usage.updated 累计)
  usageEl.textContent =
    `本轮 ${state.usage.turnTokens.toLocaleString()} tokens · ` +
    `会话 ${state.usage.sessionTokens.toLocaleString()} tokens · ` +
    `模型请求 ${state.usage.requests} 次`;

  errorEl.hidden = state.lastError === undefined;
  if (state.lastError !== undefined) {
    errorEl.textContent = `${state.lastError.code ?? 'error'}:${state.lastError.message}`;
  }

  hasMessages = state.messages.length > 0;
  placeholderEl.hidden = hasMessages || hasRestored;
  const nearBottom =
    transcriptEl.scrollTop + transcriptEl.clientHeight >= transcriptEl.scrollHeight - 40;
  // 只清直接子级的活会话消息::scope 限定恒不误删 #restored-section 里的
  // 已恢复历史气泡(恢复区不属于本状态快照,由 onRestored 一次性投影)
  for (const stale of transcriptEl.querySelectorAll(':scope > .message')) stale.remove();
  const lastMessage = state.messages[state.messages.length - 1];
  for (const message of state.messages) {
    transcriptEl.append(
      renderMessage(message, state.activeTools, {
        isLast: message === lastMessage,
        status: state.status,
        images: state.userImages?.[message.id] ?? [],
      }),
    );
  }
  if (nearBottom) transcriptEl.scrollTop = transcriptEl.scrollHeight;

  // 待办面板(TodoWrite 台账;plan.todo.updated 事件的投影)
  renderTodos(state.todos);
}

// ———————————————————————— 「试一试」快捷入口 ————————————————————————
// 每颗芯片驱动一项能力;可用性由能力面板数据(位 + 装配)决定——位关的
// 置灰并注明,「受能力位裁剪」在输入口就看得见。

const CHIPS = [
  { label: '应用信息', need: (i) => i.assembly.custom.length > 0, why: 'customTools 位', prompt: '帮我看看应用信息' },
  { label: '改窗口标题', need: (i) => i.assembly.custom.length > 0, why: 'customTools 位', prompt: '把窗口标题改成「tansr 全能力演示」' },
  { label: '读文件', need: (i) => i.assembly.builtin.includes('read'), why: 'read 位', prompt: '读一下工作区里的 README.txt,原样给我内容' },
  { label: '写文件', need: (i) => i.assembly.builtin.includes('write'), why: 'write 位', prompt: '在工作区新建 hello.txt,内容写:你好 tansr' },
  { label: '编辑文件', need: (i) => i.assembly.builtin.includes('edit') && i.assembly.builtin.includes('read'), why: 'edit 位', prompt: '把工作区 notes.md 里的 TODO 改成 DONE(先读再改)' },
  { label: 'Glob 检索', need: (i) => i.assembly.builtin.includes('glob'), why: 'glob 位', prompt: '用 Glob 找出工作区所有 .md 文件' },
  { label: 'Grep 检索', need: (i) => i.assembly.builtin.includes('grep'), why: 'grep 位', prompt: '用 Grep 在工作区里找含「tansr」的行' },
  { label: '列目录', need: (i) => i.assembly.builtin.includes('list'), why: 'list 位', prompt: '列一下工作区根目录都有什么' },
  { label: '跑命令', need: (i) => i.assembly.builtin.includes('shell'), why: 'shell 位', prompt: '跑一下 node --version,把输出告诉我' },
  { label: '抓网页', need: (i) => i.assembly.builtin.includes('webFetch'), why: 'webFetch 位', prompt: '抓取 https://example.com 并用一句话总结' },
  { label: 'HTTP 请求', need: (i) => i.assembly.builtin.includes('http'), why: 'http 位', prompt: '用 Http 工具 GET https://example.com,报告状态码' },
  { label: '记待办', need: (i) => i.assembly.builtin.includes('todoWrite'), why: 'todoWrite 位', prompt: '用 TodoWrite 记三条待办:研究 SDK、写集成、上线发布;第一条标记进行中' },
  { label: '问我问题', need: (i) => i.assembly.builtin.includes('askUser'), why: 'askUser 位', prompt: '用 AskUser 问我今晚吃什么,给三个选项:火锅、烧烤、沙拉' },
  { label: '派子代理', need: (i) => i.assembly.task, why: 'agent 位', prompt: '派一个子代理(Task),让它数数工作区有几个文件,回报结果' },
  { label: '技能:目录装载', need: (i) => i.assembly.skill, why: 'skills 位', prompt: '装载 electron-tips 技能,然后回答:Electron 应用怎么防提示词注入?' },
  { label: '技能:内联', need: (i) => i.assembly.skill, why: 'skills 位', prompt: '装载 session-summary 技能,给本次会话来个摘要' },
  { label: 'MCP 记笔记', need: (i) => i.assembly.mcp, why: 'mcp 位', prompt: '用 MCP 记一条笔记:明天做验收;然后列出全部笔记' },
  // 媒体四工具(doc/128:kernel 环2 工具 + 平台提供方)按 platform.<name> 位列入 tools.builtin,
  // 装配态看 assembly.media；ASR 也有独立录音/文件转写入口，转写先入草稿而不自动发送。
  {
    label: '画一张图',
    need: (i) => i.assembly.media.includes('imageGen'),
    why: 'platform.imageGen 位',
    prompt: '画一张雪山日出',
  },
  {
    label: '生成视频',
    need: (i) => i.assembly.media.includes('videoGen'),
    why: 'platform.videoGen 位',
    prompt: '生成一段 4 秒的海浪视频',
  },
  {
    label: '读出来(TTS)',
    need: (i) => i.assembly.media.includes('textToSpeech'),
    why: 'platform.textToSpeech 位',
    prompt: '把这句话读出来:你好,欢迎使用探针。',
  },
  {
    label: '联网搜索',
    // S-WS2:联网搜索 = 环2 WebSearch 骑平台通道,装配态看 builtin(面板双位
    // 过滤后:tools.webSearch + platform.webSearch)。
    need: (i) => i.assembly.builtin.includes('webSearch'),
    why: 'tools.webSearch + platform.webSearch 双位',
    prompt: '联网搜索「tansr SDK」,给我三条来源(标题加链接)',
  },
];

function renderChips() {
  for (const stale of chipsEl.querySelectorAll('button')) stale.remove();
  for (const chip of CHIPS) {
    const enabled = info !== null && chip.need(info);
    const btn = el('button', `chip${enabled ? '' : ' chip-off'}`, chip.label);
    btn.type = 'button';
    btn.disabled = !enabled;
    btn.title = enabled ? chip.prompt : t('tools.unavailableNote');
    if (enabled) {
      btn.addEventListener('click', () => {
        if (inputEl.disabled) return;
        inputEl.value = chip.prompt;
        composerEl.requestSubmit();
      });
    }
    chipsEl.append(btn);
  }
}

// ———————————————————————— 能力面板渲染 ————————————————————————

const TOOL_BIT_ORDER = [
  'read', 'write', 'edit', 'glob', 'grep', 'list', 'shell', 'process',
  'webFetch', 'webSearch', 'http', 'todoWrite', 'askUser', 'agent', 'skills', 'mcp', 'customTools',
];
// 平台位 5(doc/123 音频两位;bundle 五键恒在场):四媒体位 = 同名 kernel 媒体工具的装配位,
// webSearch 位 = 环2 WebSearch 的平台通道位
const PLATFORM_BIT_ORDER = ['imageGen', 'videoGen', 'webSearch', 'speechToText', 'textToSpeech'];

function renderCaps(caps) {
  for (const stale of capsGridEl.querySelectorAll('.cap-bit')) stale.remove();
  for (const name of TOOL_BIT_ORDER) {
    const on = caps.tools[name] === true;
    const bit = el('span', `cap-bit${on ? ' on' : ''}`, `tools.${name}`);
    bit.title = on ? '位开' : '位关(控制台 → 应用 → 能力与配额 可开)';
    capsGridEl.append(bit);
  }
  for (const name of PLATFORM_BIT_ORDER) {
    const on = caps.platform[name] === true;
    const bit = el('span', `cap-bit platform${on ? ' on' : ''}`, `platform.${name}`);
    bit.title = on ? '位开' : '位关(控制台 → 应用 → 能力与配额 可开)';
    capsGridEl.append(bit);
  }
  const offCount = [...TOOL_BIT_ORDER.map((n) => caps.tools[n]), ...PLATFORM_BIT_ORDER.map((n) => caps.platform[n])].filter((v) => v !== true).length;
  capsNoteEl.textContent =
    offCount === 0
      ? '全部能力位已开。位 = 装配期「App 有没有」,运行时权限 = 「这一次许不许」,两层正交。'
      : `${offCount} 个位处于关态:对应工具静默不装(缺席选择)/材料拒装(显式选择报 capability_disabled),上方灰芯片即降级可见面。`;
}

function renderAssembly(assembly) {
  for (const stale of assemblyListEl.querySelectorAll('li')) stale.remove();
  const active = new Set([...assembly.builtin, ...assembly.media]);
  const groups = [
    ['local', ['read', 'write', 'edit', 'glob', 'grep', 'list', 'shell', 'todoWrite', 'askUser']],
    ['network', ['webFetch', 'webSearch', 'http']],
    ['media', ['imageGen', 'videoGen', 'speechToText', 'textToSpeech']],
  ];
  for (const [group, names] of groups) {
    const row = el('li', 'tool-group');
    row.dataset.toolGroup = group;
    row.append(el('strong', '', t(`tools.${group}`)));
    const items = el('ul', 'system-tool-items');
    for (const name of names) {
      const assembled = active.has(name);
      const item = el('li', `system-tool${assembled ? ' enabled' : ''}`);
      item.dataset.tool = name;
      item.dataset.assembled = String(assembled);
      const status = assembled && group === 'media' && info?.media[name]?.length === 0
        ? 'tools.unconfigured' : assembled ? 'tools.enabled' : 'tools.disabled';
      item.append(el('span', 'system-tool-name', t(`tools.${name}`)), el('span', 'system-tool-status', t(status)));
      items.append(item);
    }
    row.append(items);
    assemblyListEl.append(row);
  }
  const rows = [
    [t('tools.custom'), assembly.custom.join(' · ') || t('tools.disabled')],
    [t('tools.task'), t(assembly.task ? 'tools.attached' : 'tools.unattached')],
    [t('tools.skill'), t(assembly.skill ? 'tools.attached' : 'tools.unattached')],
    [t('tools.mcp'), t(assembly.mcp ? 'tools.attached' : 'tools.unattached')],
  ];
  for (const [k, v] of rows) {
    const li = el('li', 'panel-kv');
    li.append(el('span', 'panel-k', k));
    li.append(el('span', 'panel-v', v));
    assemblyListEl.append(li);
  }
}

/**
 * 媒体授权模型面板(S-G1 媒体池化;bundle platformModels 四键恒在场):授权集如实呈现,
 * 顺位第一标「缺省」(model 不点名即用它);空集 = 平台未配置(工具照装,调用回结构化错)。
 */
function renderMediaPanel() {
  for (const stale of mediaListEl.querySelectorAll('li')) stale.remove();
  const rows = [
    ['图像(imageGen)', info.media.imageGen],
    ['视频(videoGen)', info.media.videoGen],
    ['语音转文字(speechToText)', info.media.speechToText],
    ['文字转语音(textToSpeech)', info.media.textToSpeech],
  ];
  for (const [label, list] of rows) {
    const li = el('li', 'panel-kv');
    li.append(el('span', 'panel-k', label));
    li.append(
      el(
        'span',
        'panel-v',
        list.length === 0
          ? '(平台未配置)'
          : list.map((m, i) => `${m.displayName === m.model ? m.model : `${m.displayName}(${m.model})`}${i === 0 ? '【缺省】' : ''}`).join(' · '),
      ),
    );
    mediaListEl.append(li);
  }
}

function renderUsagePanel() {
  if (info === null) return;
  if (info.usage === null) {
    usagePanelEl.textContent = '尚未取到用量(/v1/my-usage)。';
  } else {
    const u = info.usage;
    usagePanelEl.textContent =
      `endUser ${u.endUserId} · 近 1 天:请求 ${u.requests} 次 · ` +
      `入 ${u.inTokens.toLocaleString()} / 出 ${u.outTokens.toLocaleString()} tokens · ` +
      `缓存读 ${u.cacheRTokens.toLocaleString()} / 写 ${u.cacheWTokens.toLocaleString()}(本面 schema 恒无金额字段)`;
  }
  quotaNoteEl.textContent = info.quotaNote;
}

function renderSkillsPanel() {
  for (const stale of skillsListEl.querySelectorAll('li')) stale.remove();
  const active = info.assembly.skill;
  for (const skill of info.skills) {
    const li = el('li', `panel-kv${active ? '' : ' off'}`);
    li.append(el('span', 'panel-k', `${skill.name}(${skill.source === 'inline' ? '内联' : '目录'})`));
    li.append(el('span', 'panel-v', active ? skill.description : `${skill.description} — skills 位关,本会话未装`));
    skillsListEl.append(li);
  }
}

function renderMcpPanel() {
  for (const stale of mcpListEl.querySelectorAll('li')) stale.remove();
  if (!info.assembly.mcp) {
    mcpListEl.append(el('li', 'panel-empty', 'mcp 位关:本会话未接 MCP(控制台开位后重启生效)。'));
    return;
  }
  if (info.mcpServers.length === 0) {
    mcpListEl.append(el('li', 'panel-empty', '连接中…(首次会话装配触发 stdio 子进程拉起)'));
    return;
  }
  for (const server of info.mcpServers) {
    const li = el('li', 'panel-kv');
    li.append(el('span', 'panel-k', `${server.name} · ${server.state}`));
    li.append(el('span', 'panel-v', server.tools.length > 0 ? server.tools.join(' · ') : '(工具目录待发现)'));
    mcpListEl.append(li);
  }
}

function renderSystemPrompt(source) {
  systemPromptTitleEl.textContent = t('prompt.title');
  systemPromptSourceEl.textContent = t(source === 'platform'
    ? 'prompt.platform' : source === 'platform+sdk' ? 'prompt.both' : source === 'sdk' ? 'prompt.sdk' : source === 'none' ? 'prompt.default' : 'prompt.pending');
  systemPromptNoteEl.textContent = t('prompt.refresh');
}

function renderInfo() {
  if (info === null) return;
  renderCatalogLanguage();
  renderCaps(info.capabilities);
  renderAssembly(info.assembly);
  renderSystemPrompt(info.systemPrompt?.source);
  renderMediaPanel();
  renderUsagePanel();
  renderSkillsPanel();
  renderMcpPanel();
  renderChips();

  // 模型切换器(bundle 目录;切后下一轮生效)。模型呈现面二波(2026-08-30 ④):
  // displayName 主显(与 handle 同串只显一次恒不重复);任一条目携 manufacturer
  // 即按厂商 optgroup 分组(未标注行归「其他/未分组」殿后);旧 bundle/旧版
  // SDK 无维 = 平铺现状,恒不断链。选定值恒为 handle(setModel 语义不变)。
  for (const stale of modelSelectEl.querySelectorAll('option, optgroup')) stale.remove();
  const optionLabel = (model) =>
    model.displayName && model.displayName !== model.handle
      ? `${model.displayName}(${model.handle})`
      : model.handle;
  const appendOption = (parent, model) => {
    const opt = el('option', '', optionLabel(model));
    opt.value = model.handle;
    parent.append(opt);
  };
  if (info.models.some((m) => m.manufacturer)) {
    const groups = new Map();
    const ungrouped = [];
    for (const model of info.models) {
      if (!model.manufacturer) {
        ungrouped.push(model);
        continue;
      }
      if (!groups.has(model.manufacturer)) groups.set(model.manufacturer, []);
      groups.get(model.manufacturer).push(model);
    }
    for (const [label, models] of groups) {
      const group = document.createElement('optgroup');
      group.label = label;
      for (const model of models) appendOption(group, model);
      modelSelectEl.append(group);
    }
    if (ungrouped.length > 0) {
      const group = document.createElement('optgroup');
      group.label = '其他/未分组';
      for (const model of ungrouped) appendOption(group, model);
      modelSelectEl.append(group);
    }
  } else {
    for (const model of info.models) appendOption(modelSelectEl, model);
  }
  modelSelectEl.value = info.currentModel;
  modelSelectEl.disabled = info.models.length === 0;

  persistenceToggleEl.checked = info.persistence;
  persistenceToggleEl.disabled = false;
  persistenceNoteEl.textContent = info.persistence
    ? t('history.on').replace('{path}', info.paths.historyFile)
    : t('history.off');

  // 呈现档(S-V1 delivery):随时可切,块粒度即时生效
  deliveryTextEl.value = info.viewDelivery.text;
  deliveryThinkingEl.value = info.viewDelivery.thinking;
  deliveryTextEl.disabled = false;
  deliveryThinkingEl.disabled = false;
  deliveryNoteEl.textContent = info.viewDelivery.note;

  // 思考生成(生成面 setThinking;下一轮生效)
  thinkingToggleEl.checked = info.thinkingGen.on;
  thinkingToggleEl.disabled = false;
  thinkingNoteEl.textContent = info.thinkingGen.note;
}

// ———————————————————————— 事件接线 ————————————————————————

api.onState(render);

api.onInfo((payload) => {
  info = payload;
  renderInfo();
  renderContext();
  renderSpeechModels();
});

function renderSpeechModels() {
  const select = document.getElementById('speech-model');
  const selected = select.value; select.replaceChildren();
  for (const model of info.media.textToSpeech) {
    const option = el('option','',`${model.displayName} · ${model.maxChars ?? '?'} ${t('speech.weighted')}${model.inputLimit ? ` · ${model.inputLimit.max} ${t(`speech.${model.inputLimit.unit}`)}` : ''}`); option.value = model.model; select.append(option);
  }
  if (info.media.textToSpeech.some(model => model.model === selected)) select.value = selected;
}

function renderContext() {
  const target = document.getElementById('context-state');
  const context = info?.context;
  target.replaceChildren();
  if (!context) { target.textContent = t('context.unknown'); return; }
  const model = value => value ? `${value.provider} / ${value.model}` : t('context.unknown');
  const number = value => typeof value === 'number' ? value.toLocaleString() : t('context.unknown');
  const budget = context.budget;
  const rows = [
    ['context.selected', `${model(context.selected.model)}${context.selected.alias ? ` (${context.selected.alias})` : ''}`],
    ['context.active', model(context.active?.model)], ['context.observed', model(context.lastObserved?.model)],
    ['context.window', `${number(budget.physicalWindowTokens)} / ${number(budget.effectiveWindowTokens)}`],
    ['context.used', `${number(budget.estimatedInputTokens)} / ${number(budget.remainingTokens)}`],
    ['context.reserve', `${number(budget.outputReserveTokens)} / ${number(budget.thinkingReserveTokens)}`],
    ['context.source', budget.settingsSource], ['context.transition', context.transition.status],
    ['context.thresholds', budget.thresholds ? ['microcompactAt','warningAt','autoCompactAt','blockingAt'].map(key => number(budget.thresholds[key])).join(' / ') : t('context.unknown')],
  ];
  for (const [key, value] of rows) target.append(el('div', '', `${t(key)}: ${value}`));
  modelSelectEl.disabled = context.transition.status !== 'idle' || info.models.length === 0;
}

api.onNarrator((line) => {
  const nearBottom =
    narratorLogEl.scrollTop + narratorLogEl.clientHeight >= narratorLogEl.scrollHeight - 30;
  narratorLogEl.append(el('div', 'narrator-line', String(line)));
  while (narratorLogEl.childElementCount > 400) narratorLogEl.firstElementChild.remove();
  if (nearBottom) narratorLogEl.scrollTop = narratorLogEl.scrollHeight;
});

api.onRestored((rows) => {
  if (!Array.isArray(rows) || rows.length === 0) return;
  hasRestored = true;
  restoredEl.hidden = false;
  placeholderEl.hidden = true;
  for (const row of rows) {
    const wrap = el('div', `message ${row.role === 'user' ? 'user' : 'assistant'} restored`);
    wrap.append(renderTextBubble(String(row.text)));
    for (const src of row.images ?? []) appendImage(wrap, src, '', 'gen-image');
    restoredEl.append(wrap);
  }
});

api.onPhase(({ phase, detail }) => {
  if (phase !== 'ready') {
    // 连接未就绪时撤销媒体 UI 所有权；迟到 IPC 不能恢复旧草稿。
    speechEpoch += 1; speechPlan = null; speechCancelled = true;
    document.getElementById('resume-speech').hidden = true;
    if (audioBusy) mediaStatusEl.textContent = t('speech.stopped');
  }
  connected = phase === 'ready'; updateMediaControls();
  if (phase === 'connecting') {
    phaseEl.textContent = detail ?? '连接中…';
  } else if (phase === 'ready') {
    // detail 携装配结果注记(能力位计数/切模结果/持久化切换等)
    phaseEl.textContent = `已连接(平台令牌档;令牌只在主进程)${detail ? ` · ${detail}` : ''}`;
    inputEl.disabled = false;
    sendEl.disabled = false;
    inputEl.focus();
  } else if (phase === 'fatal') {
    phaseEl.textContent = `连接失败:${detail ?? '未知错误'}`;
    inputEl.disabled = true;
    sendEl.disabled = true;
  }
});

api.onInfo(() => updateMediaControls());
pickImageEl.addEventListener('click', async () => {
  if (pickingImage) return;
  pickingImage = true; updateMediaControls();
  try {
    const result = await api.pickImage();
    if (result) {
      imageDraft = result;
      const draft = document.getElementById('image-draft'); draft.replaceChildren();
      appendImage(draft, result.preview, result.name, 'gen-image');
      document.getElementById('remove-image').hidden = false;
      mediaStatusEl.textContent = t('image.ready');
    }
  } catch (error) { mediaStatusEl.textContent = t('image.failed') + error.message; }
  finally { pickingImage = false; updateMediaControls(); }
});
document.getElementById('remove-image').addEventListener('click', () => {
  imageDraft = null; document.getElementById('image-draft').replaceChildren(); document.getElementById('remove-image').hidden = true;
});
transcribeFileEl.addEventListener('click', () => runAudio(() => api.transcribeFile()));
speakInputEl.addEventListener('click', () => runSpeech(inputEl.value));
cancelAudioEl.addEventListener('click', () => {
  speechCancelled = true;
  recordingCancelled = true;
  if (recorder) { recorder.stop(); recorder = null; clearInterval(recordingTimer); recordAudioEl.textContent = t('media.record'); mediaStatusEl.textContent = '已取消录音'; updateMediaControls(); }
  api.cancelAudio();
});
async function stopRecording() {
  if (!recorder) return;
  const bytes = recorder.stop(); recorder = null; clearInterval(recordingTimer); recordAudioEl.textContent = t('media.record');
  if (!bytes || bytes.length <= 44) { mediaStatusEl.textContent = '未采集到音频'; updateMediaControls(); return; }
  const uri = await new Promise((resolve, reject) => {
    const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject;
    reader.readAsDataURL(new Blob([bytes], { type: 'audio/wav' }));
  });
  await runAudio(() => api.transcribeRecording(uri));
}
recordAudioEl.addEventListener('click', async () => {
  if (recorder) { await stopRecording(); return; }
  recordingCancelled = false;
  audioBusy = true; updateMediaControls();
  try {
    recorder = await window.createDemoRecorder(stopRecording);
    if (recordingCancelled) { recorder.stop(); recorder = null; mediaStatusEl.textContent = '已取消录音'; return; }
    const start = Date.now(); recordAudioEl.textContent = t('media.stopRecording');
    recordingTimer = setInterval(() => { mediaStatusEl.textContent = `正在录音 ${Math.floor((Date.now() - start) / 1000)} / 60 秒`; }, 250);
  } catch (error) { mediaStatusEl.textContent = `录音失败：${error.message}`; }
  finally { audioBusy = false; updateMediaControls(); }
});
window.addEventListener('beforeunload', () => { recorder?.stop(); clearInterval(recordingTimer); api.cancelAudio(); });

let submittingInput = false;
composerEl.addEventListener('submit', async (event) => {
  event.preventDefault();
  const text = inputEl.value.trim();
  if ((text === '' && !imageDraft) || submittingInput || pickingImage) return;
  if (imageDraft && inputTurnBusy) { inputReceiptEl.textContent = t('image.busy'); return; }
  if (inputTurnBusy && !inputTarget) { inputReceiptEl.textContent = '本轮暂不可接纳，请稍后重试'; return; }
  const submittedImage = imageDraft;
  const input = { inputId: crypto.randomUUID(), text, target: inputTurnBusy ? inputTarget : null,
    imageIds: submittedImage ? [submittedImage.id] : [] };
  submittingInput = true;
  inputReceiptEl.textContent = input.target ? '正在提交补充说明…' : '正在发送…';
  try {
    const result = await api.submitInput(input);
    if (result.outcome === 'started' || result.outcome === 'accepted') {
      if (inputEl.value.trim() === text) inputEl.value = '';
      if (imageDraft === submittedImage) {
        imageDraft = null; document.getElementById('image-draft').replaceChildren(); document.getElementById('remove-image').hidden = true;
      }
      inputReceiptEl.textContent = result.outcome === 'started' ? '已开始新一轮' : INPUT_STATE_LABEL[result.receipt.state];
    } else inputReceiptEl.textContent = '未提交：' + result.code + '。原文已保留，请确认后重新发送。';
  } catch { inputReceiptEl.textContent = '未取得回执，原文已保留；请先确认会话状态。'; }
  finally { submittingInput = false; }
});

stopEl.addEventListener('click', () => api.interrupt());

modelSelectEl.addEventListener('change', () => {
  if (info !== null && modelSelectEl.value !== '' && modelSelectEl.value !== info.currentModel) {
    api.setModel(modelSelectEl.value);
  }
});

persistenceToggleEl.addEventListener('change', () => {
  api.setPersistence(persistenceToggleEl.checked);
});

deliveryTextEl.addEventListener('change', () => {
  api.setDelivery({ text: deliveryTextEl.value });
});

deliveryThinkingEl.addEventListener('change', () => {
  api.setDelivery({ thinking: deliveryThinkingEl.value });
});

thinkingToggleEl.addEventListener('change', () => {
  api.setThinking(thinkingToggleEl.checked);
});

usageRefreshEl.addEventListener('click', () => api.refreshUsage());

// ———— 权限确认框(主进程 askUser 桥的 UI 半边)————

let currentPermissionId = null;

api.onPermissionAsk(({ id, name, args }) => {
  currentPermissionId = id;
  permNameEl.textContent = name;
  permArgsEl.textContent = args === undefined ? '(无参数)' : compactJson(args, 600);
  permBackdropEl.hidden = false;
  permAllowEl.focus();
});

api.onPermissionClosed(({ id }) => {
  if (currentPermissionId === id) {
    currentPermissionId = null;
    permBackdropEl.hidden = true;
  }
});

function answerPermission(allow) {
  if (currentPermissionId === null) return;
  api.answerPermission(currentPermissionId, allow);
  currentPermissionId = null;
  permBackdropEl.hidden = true;
}

permAllowEl.addEventListener('click', () => answerPermission(true));
permDenyEl.addEventListener('click', () => answerPermission(false));

// 同轮补充和审批各自提交；文本、Enter、粘贴均不会触发允许/拒绝按钮。
function addDialogSupplement(card) {
  const box = el('div', 'q-block');
  const input = el('textarea', 'q-freetext');
  input.placeholder = '补充本轮任务说明（不会答复审批或提问）';
  input.setAttribute('aria-label', '补充本轮任务说明');
  const button = el('button', '', '提交补充说明');
  button.type = 'button';
  const status = el('p', '');
  status.setAttribute('role', 'status');
  input.addEventListener('keydown', event => event.stopPropagation());
  button.addEventListener('click', async event => {
    event.preventDefault(); event.stopPropagation();
    const text = input.value.trim();
    if (!text || button.disabled) return;
    const target = inputTarget;
    if (!target) { status.textContent = '当前轮暂不可接纳，原文已保留'; return; }
    button.disabled = true;
    try {
      const result = await api.submitInput({ inputId: crypto.randomUUID(), text, target });
      status.textContent = result.outcome === 'accepted' ? INPUT_STATE_LABEL[result.receipt.state] : '未提交：' + result.code;
      if (result.outcome === 'accepted' && input.value.trim() === text) input.value = '';
    } catch { status.textContent = '未取得回执，原文已保留'; }
    finally { button.disabled = false; }
  });
  box.append(input, button, status); card.append(box);
}
addDialogSupplement(permBackdropEl.querySelector('.permission-card'));
addDialogSupplement(questionBackdropEl.querySelector('.permission-card'));

// ———— 提问对话框(AskUser 工具 PromptChannel 桥的 UI 半边)————
// 与权限框是两条独立注入缝:权限答「许不许」,这里答「问题选什么/补什么」。

let currentQuestion = null; // { id, questions, picks: Map<qid, Set<optionId>> }

function renderQuestionDialog(payload) {
  currentQuestion = { id: payload.id, questions: payload.questions, picks: new Map() };
  for (const stale of [...questionBodyEl.children]) stale.remove();
  for (const q of payload.questions) {
    const block = el('div', 'q-block');
    block.append(el('p', 'q-prompt', q.prompt));
    const picks = new Set();
    currentQuestion.picks.set(q.id, picks);
    const optionsRow = el('div', 'q-options');
    for (const option of q.options) {
      const btn = el('button', 'q-option', option.label);
      btn.type = 'button';
      btn.dataset.optionId = option.id;
      btn.addEventListener('click', () => {
        if (q.allowMultiple) {
          if (picks.has(option.id)) picks.delete(option.id);
          else picks.add(option.id);
          btn.classList.toggle('picked');
        } else {
          picks.clear();
          picks.add(option.id);
          for (const sibling of optionsRow.querySelectorAll('.q-option')) sibling.classList.remove('picked');
          btn.classList.add('picked');
        }
      });
      optionsRow.append(btn);
    }
    block.append(optionsRow);
    const free = el('input', 'q-freetext');
    free.type = 'text';
    free.placeholder = '补充说明(可选,与选项并存)';
    free.dataset.questionId = q.id;
    block.append(free);
    questionBodyEl.append(block);
  }
  questionBackdropEl.hidden = false;
}

api.onQuestionAsk((payload) => renderQuestionDialog(payload));

api.onQuestionClosed(({ id }) => {
  if (currentQuestion !== null && currentQuestion.id === id) {
    currentQuestion = null;
    questionBackdropEl.hidden = true;
  }
});

questionSubmitEl.addEventListener('click', () => {
  if (currentQuestion === null) return;
  const answers = currentQuestion.questions.map((q) => {
    const freeEl = questionBodyEl.querySelector(`input[data-question-id="${q.id}"]`);
    const freeText = freeEl !== null ? freeEl.value.trim() : '';
    return {
      questionId: q.id,
      selectedOptionIds: [...(currentQuestion.picks.get(q.id) ?? [])],
      ...(freeText !== '' ? { freeText } : {}),
    };
  });
  api.answerQuestion(currentQuestion.id, answers);
  currentQuestion = null;
  questionBackdropEl.hidden = true;
});

// 初始侧栏先按页面语言显示等待状态，不等首个 chat:info。
function renderCatalogLanguage() {
  for (const node of document.querySelectorAll('[data-media-i18n]')) node.textContent = t(node.dataset.mediaI18n);
  recordAudioEl.textContent = t(recorder ? 'media.stopRecording' : 'media.record');
  document.getElementById('catalog-language').value = document.documentElement.lang.toLowerCase().startsWith('en') ? 'en' : 'zh-CN';
}
document.getElementById('catalog-language').addEventListener('change', (event) => {
  document.documentElement.lang = event.target.value;
  renderCatalogLanguage();
  renderSystemPrompt(info?.systemPrompt?.source ?? null);
  if (info !== null) { renderAssembly(info.assembly); renderChips(); renderContext(); renderSpeechModels(); }
});
renderCatalogLanguage();
renderSystemPrompt(null);
// 初始芯片(未连接前全部置灰;chat:info 到达后按能力位点亮)
renderChips();
