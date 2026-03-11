import type {
  Snapshot,
  TreeEntry,
  SearchResult,
  RepoStats,
  DiffEntry,
  BackupStartBody,
  RestoreStartBody,
  ForgetStartBody,
  PruneStartBody,
  OperationStarted,
} from '@/types'

const BASE = ''  // relative to origin; Vite dev proxy handles /api

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(BASE + url)
  if (!res.ok) throw new Error(`GET ${url} failed: ${res.status} ${res.statusText}`)
  return res.json() as Promise<T>
}

/** Fetch a streaming JSON array endpoint (newline-delimited JSON objects). */
async function* streamJson<T>(url: string): AsyncGenerator<T> {
  const res = await fetch(BASE + url)
  if (!res.ok) throw new Error(`GET ${url} failed: ${res.status} ${res.statusText}`)
  const reader = res.body!.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  while (true) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const line of lines) {
      const trimmed = line.trim()
      if (trimmed && trimmed !== '[' && trimmed !== ']') {
        // Remove trailing comma if present
        const json = trimmed.replace(/,$/, '')
        yield JSON.parse(json) as T
      }
    }
  }
  if (buffer.trim() && buffer.trim() !== ']') {
    const json = buffer.trim().replace(/,$/, '')
    try { yield JSON.parse(json) as T } catch { /* empty */ }
  }
}

async function postJson<T>(url: string, body: unknown): Promise<T> {
  const res = await fetch(BASE + url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!res.ok) throw new Error(`POST ${url} failed: ${res.status} ${res.statusText}`)
  return res.json() as Promise<T>
}

export const apiClient = {
  // ── Snapshots ────────────────────────────────────────────────────────────

  streamSnapshots(): AsyncGenerator<Snapshot> {
    return streamJson<Snapshot>('/api/snapshots')
  },

  async getSnapshot(id: string): Promise<Snapshot> {
    return getJson<Snapshot>(`/api/snapshots/${id}`)
  },

  streamTree(snapshotId: string, path = '/', recursive = false): AsyncGenerator<TreeEntry> {
    const q = new URLSearchParams({ path, recursive: String(recursive) })
    return streamJson<TreeEntry>(`/api/snapshots/${snapshotId}/tree?${q}`)
  },

  streamFind(snapshotId: string, pattern: string, pathPrefix?: string): AsyncGenerator<SearchResult> {
    const q = new URLSearchParams({ pattern })
    if (pathPrefix) q.set('pathPrefix', pathPrefix)
    return streamJson<SearchResult>(`/api/snapshots/${snapshotId}/find?${q}`)
  },

  // ── Operations ────────────────────────────────────────────────────────────

  async startBackup(body: BackupStartBody): Promise<OperationStarted> {
    return postJson<OperationStarted>('/api/backup', body)
  },

  async startRestore(body: RestoreStartBody): Promise<OperationStarted> {
    return postJson<OperationStarted>('/api/restore', body)
  },

  async startForget(body: ForgetStartBody): Promise<OperationStarted> {
    return postJson<OperationStarted>('/api/forget', body)
  },

  async startPrune(body: PruneStartBody): Promise<OperationStarted> {
    return postJson<OperationStarted>('/api/prune', body)
  },

  async cancelOperation(operationId: string): Promise<void> {
    const res = await fetch(BASE + `/api/operations/${operationId}`, { method: 'DELETE' })
    if (!res.ok && res.status !== 204) {
      throw new Error(`DELETE /api/operations/${operationId} failed: ${res.status}`)
    }
  },

  // ── Stats & Diff ──────────────────────────────────────────────────────────

  async getStats(): Promise<RepoStats> {
    return getJson<RepoStats>('/api/stats')
  },

  streamDiff(snap1: string, snap2: string): AsyncGenerator<DiffEntry> {
    return streamJson<DiffEntry>(`/api/diff/${snap1}/${snap2}`)
  },
}
