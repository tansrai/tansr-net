import type { AgentSession, CreateSessionOptions, SessionStore } from '../index.js';
import type { MaterialIssue, MaterialRequest, MaterialUploadChunk, MaterialUploadStatus, MaterialResponse, MaterialReceipt } from '@tansr/kernel';
import type { ArchiveTrustedAttachmentSource, ArchiveJournalPage, ArchiveArtifactRef, ArchiveInstallationBindingView } from '@tansr/kernel';
import type { SessionInputCapabilities, SessionInputReceipt, SessionInputResult, SessionInputSubmission, SessionInputTarget } from '@tansr/kernel';

/** 原 sdk2-ext-v1 材料合同；嵌入宿主与远端宿主共用其含义。 */
export type ArchiveMaterialIssue = MaterialIssue;
/** 限定会话、来源和记录范围的上下文材料请求。 */
export type ArchiveMaterialRequest = MaterialRequest;
/** 对当前材料请求上传的有界对象分块。 */
export type ArchiveMaterialUploadChunk = MaterialUploadChunk;
/** 材料对象的上传进度与完整性状态。 */
export type ArchiveMaterialUploadStatus = MaterialUploadStatus;
/** 终端向当前请求提交的材料及完整性引用。 */
export type ArchiveMaterialResponse = MaterialResponse;
/** 材料送达及核心消费的分别确认结果。 */
export type ArchiveMaterialReceipt = MaterialReceipt;
/** 可信宿主登记并供给外置附件字节；缺省不读取路径或 URL。 */
export type ArchiveAttachmentSource = ArchiveTrustedAttachmentSource;
/** 原归档元数据页、固定来源对象引用及公开绑定视图。 */
export type ArchiveRecordPage = ArchiveJournalPage;
/** 归档中固定来源、媒体类型、字节数和摘要的对象引用。 */
export type ArchiveArtifact = ArchiveArtifactRef;
/** 当前用户获准读取的归档绑定与操作代际。 */
export type ArchiveBindingView = ArchiveInstallationBindingView;
/** Same-turn insertion contract; implementation is the original AgentSession delivery path. */
export type ArchiveInputCapabilities = SessionInputCapabilities;
/** 当前会话允许插入输入的目标范围。 */
export type ArchiveInputTarget = SessionInputTarget;
/** 交给当前运行轮处理的一次输入。 */
export type ArchiveInputSubmission = SessionInputSubmission;
/** 当前输入在内存屏障中的状态。 */
export type ArchiveInputReceipt = SessionInputReceipt;
/** 当前输入处理完成后的结果。 */
export type ArchiveInputResult = SessionInputResult;

/** 归档容量上限；字节数和记录数分别计量。 */
export interface ArchiveHostQuota { readonly bytes: number; readonly records: number }

/** 归档交接、材料读取与事件留存的有限上限，单位由字段名标明。 */
export interface ArchiveHostLimits {
  readonly controlBytes: number; readonly recordBytes: number; readonly pageRecords: number; readonly pageBytes: number;
  readonly attachmentBytes: number; readonly chunkBytes: number; readonly materialConcurrent: number; readonly materialQueue: number;
  readonly materialCandidates: number; readonly materialBytes: number; readonly materialDeadlineMs: number;
  readonly pendingRecords: number; readonly pendingBytes: number; readonly inflightReserveBytes: number; readonly offlineMs: number;
  readonly eventRetentionMs: number; readonly eventRetentionFrames: number; readonly eventRetentionBytes: number;
  readonly terminalReceiptRetentionMs: number; readonly epochLifetimeMs: number; readonly materialChunkBytes: number;
}

/** 待完成工作的授权请求；仅描述工作身份，本对象不授予权限。 */
export interface ArchiveCompletionAuthorization {
  readonly operation: 'finish-run' | 'finish-rewrite';
  readonly scope: { readonly applicationScopeId: string; readonly endUserId: string; readonly authorizationRevision: string };
  readonly sessionId: string;
  readonly originalOwnerEpoch: string;
  readonly workId: string;
}

/** 当前应用内的终端用户与会话标识。 */
export interface NodeArchiveSubject { readonly endUserId: string; readonly sessionId: string }
/** 已确认有效历史的有界引用；不含正文，不授予执行/访问权限，不证明模型缓存命中。 */
export interface ArchiveEffectiveContextReference {
  readonly format: 'sdk2-effective-context-v1'; readonly referenceDigest: string;
  /** 当前读取授权的修订；不是完整模型策略见证。 */
  readonly scope: { readonly applicationScopeId: string; readonly endUserId: string; readonly authorizationRevision: string };
  readonly bindingId: string; readonly sourceId: string; readonly sourceGeneration: string;
  readonly target: { readonly sessionId: string; readonly sourceSnapshotDigest: string;
    readonly generations: { readonly historyEpoch: string; readonly deletionGeneration: string; readonly projectionRevision: string } };
  readonly storeCommitId: string;
  readonly completion: { readonly kind: 'installation' | 'run' | 'rewrite'; readonly eventDigest: string; readonly workId: string | null };
}
/**
 * 当前归档宿主真实启用的存储策略视图。
 *
 * 这是只读能力声明，不是跨设备同步或多副本耐久证明。SDK2-03 尚未
 * 启用共享来源、同步副本或离线删除传播；调用方应据此明确把这些能力
 * 视为未配置，而不能从 `source-ack-with-durable-spool` 推导出来。
 */
export interface ArchiveStorageStrategyView {
  readonly format: 'sdk2-storage-strategy-v1';
  readonly strategy: 'single-authorized-source';
  readonly sourceId: string;
  readonly sourceGeneration: string;
  readonly durability: 'source-ack-with-durable-spool';
  readonly delivery: 'required';
  readonly sessionAvailability: 'legacy-complete';
  readonly replication: 'none';
  readonly synchronization: 'none';
}
/** 宿主在执行操作前向应用请求的权限种类。 */
export type NodeArchiveOperation = 'open' | 'resume' | 'read' | 'run' | 'transfer' | 'materials' | 'close-overlay' | 'dispose';

/** 本地归档宿主配置；Store 必须来自同一 SDK 实例的文件 Store 工厂且未配置冷层。 */
export interface NodeArchiveHostOptions {
  readonly store: SessionStore;
  readonly applicationScopeId: string;
  readonly archive: {
    readonly directory: string; readonly mode: 'create' | 'reopen'; readonly hostId: string;
    /** host/application/endUser 为聚合上限；capture/source 为每个会话两座物理库的固定额度。 */
    readonly quota: { readonly host: ArchiveHostQuota; readonly application: ArchiveHostQuota; readonly endUser: ArchiveHostQuota;
      readonly capture: ArchiveHostQuota; readonly source: ArchiveHostQuota };
    readonly maxSessions: number; readonly commandBytes: number; readonly commandDatabasePages: number;
    readonly spoolDatabasePages: number; readonly limits: ArchiveHostLimits;
    /** 显式启用原上下文材料链；缺省不增加材料配额篮或材料读写。 */
    readonly contextMaterials?: boolean;
  };
  /** 同步核实当前用户权限并返回当前授权修订；拒绝时抛错，不接受异步回调。 */
  readonly authorize: (input: { readonly subject: NodeArchiveSubject; readonly operation: NodeArchiveOperation }) => {
    readonly authorizationRevision: string;
  };
  /** 独立批准完成已受理的工作；普通读取或恢复授权不会隐式授予此权限。 */
  readonly authorizeCompletion?: (input: ArchiveCompletionAuthorization) => void;
  /** 独立批准原创建续办；在介质读取前同步执行，不由普通恢复权限推导。 */
  readonly authorizeContinuation?: (input: { readonly scope: ArchiveCompletionAuthorization['scope']; readonly sessionId: string }) => void;
  /** 每会话显式登记可信外置来源；同步选择源，读取仍沿原捕获预算与取消链。 */
  readonly trustedAttachmentSourceFor?: (subject: NodeArchiveSubject) => ArchiveAttachmentSource | undefined;
}

/** 新建会话的选项；持久化 Store 和会话 ID 统一由宿主管理。 */
export type NodeArchiveFreshSessionOptions = Omit<CreateSessionOptions, 'store' | 'sessionId' | 'resume' | 'fork'>;
/** 恢复会话的选项；从原 Store 读取历史，不额外注入初始历史。 */
export type NodeArchiveResumeSessionOptions = Omit<NodeArchiveFreshSessionOptions, 'initialMessages'>;
/** 新建或恢复原会话；恢复未完成工作须显式选择 finishPending 并提供独立完成授权。 */
export type NodeArchiveOpenRequest =
  { readonly mode: 'fresh'; readonly subject: NodeArchiveSubject; readonly session: NodeArchiveFreshSessionOptions }
  | { readonly mode: 'resume'; readonly subject: NodeArchiveSubject; readonly session: NodeArchiveResumeSessionOptions;
    readonly finishPending?: boolean; readonly continuePrepared?: boolean };

/** 归档宿主返回原 AgentSession；归档交接与会话执行使用同一会话和持久化实现。 */
export interface NodeArchiveHost {
  /** 完成归档安装或恢复后返回原会话；构造宿主本身不会创建数据库。 */
  open(input: NodeArchiveOpenRequest): Promise<AgentSession>;
  /** 仅空闲已安装会话；重读原Store并核原完成事实，未决时拒绝而不补写或重放。 */
  readEffectiveContext(subject: NodeArchiveSubject): Promise<ArchiveEffectiveContextReference>;
  /** 等待持久交接和 ACK；pending 为 true 时还有后续有界批次需要等待。 */
  flushArchive(subject: NodeArchiveSubject): Promise<{ readonly pending: boolean; readonly batches: number }>;
  /** 当前授权下的公开绑定视图，含原操作代际；不暴露内部所有者或控制事实。 */
  readArchiveBinding(subject: NodeArchiveSubject): ArchiveBindingView;
  /**
   * 读取当前绑定实际启用的存储策略；`replication`/`synchronization` 为
   * `none` 时，不能把本地来源视为已具备多设备共享或备份能力。
   */
  readArchiveStorageStrategy(subject: NodeArchiveSubject): ArchiveStorageStrategyView;
  /** 只读取原来源的有界元数据页；不检索全史、不访问任意路径。 */
  readArchiveRecords(subject: NodeArchiveSubject, input: { readonly afterSequence: string | null; readonly limit: number; readonly maxBytes: number }): ArchiveRecordPage;
  /** 只读取已登记引用的完整字节并复核原 SHA/MIME/长度；单对象沿原 32MiB 帽。 */
  readArchiveArtifact(subject: NodeArchiveSubject, artifact: ArchiveArtifact): Uint8Array;
  /** 查询原会话同轮接纳能力；不创建新轮、不刷新系统提示词。 */
  inputCapabilities(subject: NodeArchiveSubject): ArchiveInputCapabilities;
  /** 读取当前原轮目标；返回 null 表示没有可插入的运行轮。 */
  getInputTarget(subject: NodeArchiveSubject): ArchiveInputTarget | null;
  /** 将输入交给原会话注入屏障；不回落为 cancel+send。 */
  submitInput(subject: NodeArchiveSubject, input: ArchiveInputSubmission): Promise<ArchiveInputResult>;
  /** 查询原 inputId/target 的内存状态；不把它升级为持久状态。 */
  getInputStatus(subject: NodeArchiveSubject, inputId: string, target: ArchiveInputTarget): ArchiveInputReceipt | null;
  /** 请求已由核心捕获的记录；不改变当前历史或授予工具执行权限。 */
  requestMaterials(subject: NodeArchiveSubject, input: ArchiveMaterialIssue): ArchiveMaterialRequest;
  /** 复用原有界分块上传；只接收当前可信请求承诺的字节。 */
  uploadMaterialChunk(subject: NodeArchiveSubject, input: ArchiveMaterialUploadChunk): ArchiveMaterialUploadStatus;
  materialUploadStatus(subject: NodeArchiveSubject, input: { readonly materialRequestId: string; readonly artifactId: string }): ArchiveMaterialUploadStatus;
  /** 材料送达与核心消费分别记账，未知响应沿同一请求身份重试。 */
  respondMaterials(subject: NodeArchiveSubject, input: ArchiveMaterialResponse): ArchiveMaterialReceipt;
  materialStatus(subject: NodeArchiveSubject, materialRequestId: string): ArchiveMaterialReceipt;
  /** 已收到材料在原查询屏障交给同一个 ContextManager 准入。 */
  enqueueMaterials(subject: NodeArchiveSubject, input: { readonly materialRequestId: string; readonly leaseId: string; readonly required?: true }): void;
  /** 可信宿主显式取消未持有的等待；确认原拒绝/释放后移队列，未排队返回 null。 */
  cancelMaterials(subject: NodeArchiveSubject, input: { readonly materialRequestId: string }): { readonly receipt: ArchiveMaterialReceipt; readonly releasedBytes: number } | null;
  /** 交接完成后关闭归档叠加能力；同一会话仍可运行并写入原 Store，不退还固定额度。 */
  closeOverlay(subject: NodeArchiveSubject): Promise<void>;
  /** 关闭宿主介质；调用前须关闭并排空全部已返回会话，不隐式完成归档关闭或退额。 */
  dispose(): Promise<void>;
}
