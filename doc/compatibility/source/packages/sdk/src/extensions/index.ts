import { createNodeArchiveHost as createHost } from './archive-host.js';
import type { NodeArchiveHost, NodeArchiveHostOptions } from './types.js';

/** 创建显式本地归档宿主；首次 open 才初始化持久介质，会话继续使用 SDK 原执行循环。 */
export function createNodeArchiveHost(options: NodeArchiveHostOptions): NodeArchiveHost {
  return createHost(options);
}

export { NodeArchiveHostError } from './archive-host.js';
export { createNodeCacheHost } from './cache-host.js';
export type { NodeCacheHost, NodeCacheHostOptions, NodeCacheOpenRequest, NodeCacheDeleteRequest, NodeCacheLookupRequest, NodeCacheLookupResult, NodeCacheSessionOptions, NodeCacheContinuityAuthority, NodeCacheContinuityBridge, NodeCacheContinuityView } from './cache-types.js';
export type {
  ArchiveHostQuota, ArchiveHostLimits, ArchiveCompletionAuthorization, ArchiveEffectiveContextReference,
  ArchiveMaterialIssue, ArchiveMaterialRequest, ArchiveMaterialUploadChunk, ArchiveMaterialUploadStatus, ArchiveMaterialResponse, ArchiveMaterialReceipt,
  ArchiveAttachmentSource,
  ArchiveRecordPage, ArchiveArtifact, ArchiveBindingView, ArchiveStorageStrategyView,
  ArchiveInputCapabilities, ArchiveInputTarget, ArchiveInputSubmission, ArchiveInputReceipt, ArchiveInputResult,
  NodeArchiveSubject, NodeArchiveOperation, NodeArchiveHostOptions,
  NodeArchiveFreshSessionOptions, NodeArchiveResumeSessionOptions, NodeArchiveOpenRequest, NodeArchiveHost,
} from './types.js';
