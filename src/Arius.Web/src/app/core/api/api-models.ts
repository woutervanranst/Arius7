/** DTOs mirroring Arius.Api's contracts (camelCase over the wire). */

export interface AccountDto {
  id: number;
  name: string;
  repositories: number;
  hasKey: boolean;
}

export interface RepositoryDto {
  id: number;
  alias: string;
  container: string;
  accountId: number;
  account: string;
  localPath: string | null;
  defaultTier: string;
  /** Storage region the cost is priced for, resolved from the container metadata; null when it can't be read. */
  region: string | null;
  /** True when {@link region} is the fallback default because the container has no region metadata set. */
  regionIsDefault: boolean;
}

export interface SnapshotDto {
  version: string;
  timestamp: string;
  fileCount: number;
}

export interface StatisticsDto {
  files: number;
  originalSize: number;
  deduplicatedSize: number;
  storedSize: number;
  uniqueChunks: number;
  /** Estimated total monthly storage cost across all tiers, in EUR. */
  totalStorageCostPerMonth: number;
  storedByTier: TierStatisticsDto[];
}

export interface TierStatisticsDto {
  tier: string;
  uniqueChunks: number;
  storedSize: number;
  /** Estimated monthly storage cost for this tier, in EUR. */
  costPerMonth: number;
}

export interface StateFlagsDto {
  localPointer: boolean;
  localBinary: boolean;
  localDirectory: boolean;
  repository: boolean;
  repositoryHydrated: boolean;
  repositoryArchived: boolean;
  repositoryRehydrating: boolean;
}

export interface EntryDto {
  relativePath: string;
  name: string;
  kind: 'file' | 'dir';
  state: number;
  stateFlags: StateFlagsDto;
  contentHash: string | null;
  originalSize: number | null;
  created: string | null;
  modified: string | null;
}

/** Options for streaming a folder's children (mirrors ListQueryOptions). */
export interface ListEntriesOptions {
  version?: string | null;
  prefix?: string | null;
  filter?: string | null;
  includeLocal?: boolean;
}

// ── Job streaming (archive / restore) ─────────────────────────────────────────

export interface LogLine {
  ts: string;
  text: string;
  severity: 'ok' | 'warn' | 'dedup' | 'meta' | 'info';
}

// ── Jobs: absolute-state realtime + REST (Plan 2 protocol) ────────────────────

export const NON_TERMINAL_STATUSES = ['running', 'awaiting-cost', 'rehydrating'] as const;
export type JobStatus = 'running' | 'awaiting-cost' | 'rehydrating' | 'completed' | 'failed' | 'cancelled' | 'interrupted';
export const isNonTerminal = (s: string): boolean => (NON_TERMINAL_STATUSES as readonly string[]).includes(s);

/** Absolute-state progress snapshot — the `Progress` message payload AND the `AttachToJob` snapshot. Apply latest-wins. */
export interface JobSnapshot {
  jobId: string;
  phase: string;
  totalBytes: number;
  totalNewBytes: number;
  scannedBytes: number;
  hashedBytes: number;
  uploadedBytes: number;
  dedupedBytes: number;
  dedupedFiles: number;
  etaSeconds: number | null;
  throughputBytesPerSec: number;
  pct: number;
  warningCount: number;
  stats: Record<string, string>;
  // restore layers
  restoreTotalFiles: number;
  filesRestored: number;
  restoreTotalBytes: number;
  bytesRestored: number;
  chunksAvailable: number;
  chunksRehydrated: number;
  chunksNeedingRehydration: number;
  chunksPending: number;
}

export interface CostEstimateMsg {
  jobId: string;
  chunksAvailable: number;
  chunksNeedingRehydration: number;
  bytesNeedingRehydration: number;
  downloadBytes: number;
  totalStandard: number;
  totalHigh: number;
  standardWaitHours: number;
  highWaitHours: number;
}

export interface DoneMsg {
  jobId: string;
  status: string;
  summary: string;
  outcome: string | null;   // JSON of JobOutcome, or null
}

export interface JobOutcome {
  fileCount: number | null;
  uploadedBytes: number | null;
  dedupedBytes: number | null;
  filesRestored: number | null;
  downloadedBytes: number | null;
  snapshotTimestamp: string | null;
  durationSeconds: number | null;
}

export interface JobAttachState {
  status: string;
  snapshot: JobSnapshot;
  cost: CostEstimateMsg | null;
  warningCount: number;
}

export interface JobDto {
  id: string;
  repoId: number;
  repo: string;
  kind: string;     // archive | restore
  trigger: string;  // one-off | schedule
  status: string;   // JobStatus
  pct: number;
  detail: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  outcome: string | null;   // JSON of JobOutcome (history rows), or null
}

export interface JobDetailDto {
  id: string;
  repoId: number;
  repo: string;
  kind: string;
  trigger: string;
  status: string;
  pct: number;
  detail: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  outcome: string | null;
  snapshot: JobSnapshot | null;
  warningCount: number;
}

export interface JobWarningsDto {
  count: number;
  lines: string[];
  truncated: boolean;
}

export interface ScheduleDto {
  id: number;
  repoId: number;
  cron: string;
  kind: string;
  enabled: boolean;
  nextRun: string | null;
}

export interface SearchHitDto {
  repoId: number;
  repo: string;
  entry: EntryDto;
}

export interface CreateRepositoryRequest {
  accountId: number;
  container: string;
  alias: string;
  passphrase: string | null;
  localPath: string | null;
  defaultTier: string | null;
}

// ── Filesystem browse (local-path picker) ─────────────────────────────────────

/** A directory as the Arius.Api host/container sees it. */
export interface FsEntryDto {
  name: string;
  path: string;
}

/** A directory listing: the resolved path, its parent (null at the root), and immediate subdirectories. */
export interface FsListDto {
  path: string;
  parent: string | null;
  entries: FsEntryDto[];
}
