// ─── Core domain types ────────────────────────────────────────────────────────
// These mirror the C# models from Arius.Core.

export interface Snapshot {
  id: { value: string }
  time: string           // ISO 8601
  tree: { value: string }
  paths: string[]
  hostname: string
  username: string
  tags: string[]
  parent: { value: string } | null
}

export interface TreeEntry {
  path: string
  name: string
  type: TreeNodeType
  size: number
  mTime: string          // ISO 8601
  mode: string
}

export type TreeNodeType = 'file' | 'dir' | 'symlink'

// ─── Backup events ────────────────────────────────────────────────────────────

export interface BackupStarted {
  $type: 'BackupStarted'
  totalFiles: number
}

export interface BackupFileProcessed {
  $type: 'BackupFileProcessed'
  path: string
  size: number
  isDeduplicated: boolean
}

export interface BackupCompleted {
  $type: 'BackupCompleted'
  snapshot: Snapshot
  storedFiles: number
  deduplicatedFiles: number
}

export type BackupEvent = BackupStarted | BackupFileProcessed | BackupCompleted

// ─── Restore events ───────────────────────────────────────────────────────────

export interface RestorePlanReady {
  $type: 'RestorePlanReady'
  totalFiles: number
  totalBytes: number
}

export interface RestoreFileRestored {
  $type: 'RestoreFileRestored'
  path: string
  size: number
}

export interface RestoreCompleted {
  $type: 'RestoreCompleted'
  restoredFiles: number
  restoredBytes: number
}

export type RestoreEvent = RestorePlanReady | RestoreFileRestored | RestoreCompleted

// ─── Prune events ─────────────────────────────────────────────────────────────

export type PruneEventKind = 'analysing' | 'willDelete' | 'willRepack' | 'deleting' | 'repacking' | 'done'

export interface PruneEvent {
  kind: PruneEventKind
  message: string
  packId: string | null
  bytesAffected: number
}

// ─── Forget events ────────────────────────────────────────────────────────────

export type ForgetDecision = 'keep' | 'remove'

export interface ForgetEvent {
  snapshotId: string
  snapshotTime: string   // ISO 8601
  decision: ForgetDecision
  reason: string
}

// ─── Diff types ───────────────────────────────────────────────────────────────

export type DiffStatus = 'added' | 'removed' | 'modified' | 'typeChanged'

export interface DiffEntry {
  status: DiffStatus
  path: string
  oldType: TreeNodeType | null
  newType: TreeNodeType | null
  oldSize: number | null
  newSize: number | null
  oldMTime: string | null
  newMTime: string | null
}

// ─── Stats ────────────────────────────────────────────────────────────────────

export interface RepoStats {
  snapshotCount: number
  packCount: number
  totalPackBytes: number
  uniqueBlobCount: number
  uniqueBlobBytes: number
  deduplicationRatio: number
}

// ─── Search ───────────────────────────────────────────────────────────────────

export interface SearchResult {
  snapshotId: string
  snapshotTime: string   // ISO 8601
  path: string
  name: string
  type: TreeNodeType
  size: number
  mTime: string
  mode: string
}

// ─── Retention policy ────────────────────────────────────────────────────────

export interface RetentionPolicy {
  keepLast: number | null
  keepHourly: number | null
  keepDaily: number | null
  keepWeekly: number | null
  keepMonthly: number | null
  keepYearly: number | null
  keepWithin: string | null
  keepTags: string[] | null
}

// ─── API request bodies ───────────────────────────────────────────────────────

export interface BackupStartBody {
  paths: string[]
}

export interface RestoreStartBody {
  snapshotId: string
  targetPath: string
  include?: string
}

export interface ForgetStartBody {
  policy?: RetentionPolicy
  dryRun?: boolean
}

export interface PruneStartBody {
  dryRun?: boolean
}

// ─── Operation response ───────────────────────────────────────────────────────

export interface OperationStarted {
  operationId: string
}

// ─── Snapshot grouping helper ─────────────────────────────────────────────────

export interface SnapshotGroup {
  label: string          // e.g. "2026-03-11"
  snapshots: Snapshot[]
}
