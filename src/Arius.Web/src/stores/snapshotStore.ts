import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { Snapshot, SnapshotGroup } from '@/types'
import { apiClient } from '@/services/api'

export const useSnapshotStore = defineStore('snapshots', () => {
  const snapshots = ref<Snapshot[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  const selectedId = ref<string | null>(null)

  const selectedSnapshot = computed(() =>
    snapshots.value.find(s => s.id.value === selectedId.value) ?? null
  )

  /** Chronological groups (newest first by day) */
  const groups = computed<SnapshotGroup[]>(() => {
    const sorted = [...snapshots.value].sort(
      (a, b) => new Date(b.time).getTime() - new Date(a.time).getTime()
    )
    const map = new Map<string, Snapshot[]>()
    for (const snap of sorted) {
      const day = snap.time.slice(0, 10)
      if (!map.has(day)) map.set(day, [])
      map.get(day)!.push(snap)
    }
    return Array.from(map.entries()).map(([label, snaps]) => ({ label, snapshots: snaps }))
  })

  async function loadSnapshots() {
    loading.value = true
    error.value = null
    snapshots.value = []
    try {
      for await (const snap of apiClient.streamSnapshots()) {
        snapshots.value.push(snap)
      }
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      loading.value = false
    }
  }

  function selectSnapshot(id: string | null) {
    selectedId.value = id
  }

  return { snapshots, loading, error, selectedId, selectedSnapshot, groups, loadSnapshots, selectSnapshot }
})
