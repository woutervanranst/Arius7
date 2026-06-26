/** DTOs mirroring Arius.Api's contracts (camelCase over the wire). */

export interface AppInfoDto {
  version: string;
}

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
  storedByTier: TierStatisticsDto[];
}

export interface TierStatisticsDto {
  tier: string;
  uniqueChunks: number;
  storedSize: number;
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

export interface ProgressMsg {
  pct: number;
  stats: Record<string, string> | null;
}

export interface CostEstimateMsg {
  chunksAvailable: number;
  chunksNeedingRehydration: number;
  bytesNeedingRehydration: number;
  downloadBytes: number;
  totalStandard: number;
  totalHigh: number;
}

export interface DoneMsg {
  status: string;   // completed | failed
  summary: string;
}

export interface JobDto {
  id: string;
  repoId: number;
  repo: string;
  kind: string;     // archive | restore
  trigger: string;  // one-off | schedule
  status: string;   // queued | running | rehydrating | completed | failed | cancelled
  pct: number;
  detail: string | null;
  startedAt: string | null;
  finishedAt: string | null;
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
