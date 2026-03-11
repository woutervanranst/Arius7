<template>
  <div class="diff-view">
    <div v-if="loading" class="loading">Loading diff...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else>
      <div class="diff-header">
        <div class="snap-label">
          <span class="badge">From</span>
          <span class="snap-id">{{ snap1Label }}</span>
        </div>
        <div class="snap-label">
          <span class="badge">To</span>
          <span class="snap-id">{{ snap2Label }}</span>
        </div>
      </div>

      <div class="diff-summary">
        <span class="summary-item added">+{{ addedCount }} added</span>
        <span class="summary-item removed">-{{ removedCount }} removed</span>
        <span class="summary-item modified">~{{ modifiedCount }} modified</span>
        <span v-if="typeChangedCount" class="summary-item type-changed">{{ typeChangedCount }} type changed</span>
      </div>

      <div class="filter-bar">
        <label v-for="s in statuses" :key="s.value" class="filter-checkbox">
          <input type="checkbox" v-model="activeFilters" :value="s.value" />
          {{ s.label }}
        </label>
        <input
          v-model="search"
          class="search-input"
          placeholder="Filter by path..."
          type="text"
        />
      </div>

      <div class="diff-list">
        <div
          v-for="entry in filteredEntries"
          :key="entry.path"
          class="diff-entry"
          :class="entry.status"
        >
          <span class="diff-status-icon">{{ statusIcon(entry.status) }}</span>
          <span class="diff-path">{{ entry.path }}</span>
          <span v-if="entry.status === 'modified'" class="diff-size-change">
            {{ formatBytes(entry.oldSize) }} → {{ formatBytes(entry.newSize) }}
          </span>
          <span v-if="entry.status === 'added'" class="diff-size">
            {{ formatBytes(entry.newSize) }}
          </span>
          <span v-if="entry.status === 'removed'" class="diff-size">
            {{ formatBytes(entry.oldSize) }}
          </span>
        </div>
        <div v-if="filteredEntries.length === 0" class="empty-state">
          No differences match current filters.
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import type { DiffEntry, DiffStatus } from '@/types'
import { apiClient } from '@/services/api'

const props = defineProps<{
  snap1Id: string
  snap2Id: string
  snap1Label?: string
  snap2Label?: string
}>()

const entries = ref<DiffEntry[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const search = ref('')
const activeFilters = ref<DiffStatus[]>(['added', 'removed', 'modified', 'typeChanged'])

const statuses = [
  { value: 'added' as DiffStatus, label: 'Added' },
  { value: 'removed' as DiffStatus, label: 'Removed' },
  { value: 'modified' as DiffStatus, label: 'Modified' },
  { value: 'typeChanged' as DiffStatus, label: 'Type Changed' },
]

const addedCount = computed(() => entries.value.filter(e => e.status === 'added').length)
const removedCount = computed(() => entries.value.filter(e => e.status === 'removed').length)
const modifiedCount = computed(() => entries.value.filter(e => e.status === 'modified').length)
const typeChangedCount = computed(() => entries.value.filter(e => e.status === 'typeChanged').length)

const filteredEntries = computed(() => {
  return entries.value.filter(e => {
    if (!activeFilters.value.includes(e.status)) return false
    if (search.value && !e.path.toLowerCase().includes(search.value.toLowerCase())) return false
    return true
  })
})

function statusIcon(status: DiffStatus): string {
  switch (status) {
    case 'added': return '+'
    case 'removed': return '-'
    case 'modified': return '~'
    case 'typeChanged': return 'T'
    default: return '?'
  }
}

function formatBytes(bytes: number | null): string {
  if (bytes === null) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

async function loadDiff() {
  loading.value = true
  error.value = null
  entries.value = []
  try {
    for await (const entry of apiClient.streamDiff(props.snap1Id, props.snap2Id)) {
      entries.value.push(entry)
    }
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load diff'
  } finally {
    loading.value = false
  }
}

onMounted(() => loadDiff())
</script>

<style scoped>
.diff-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.diff-header {
  display: flex;
  gap: 1rem;
  padding: 0.75rem 1rem;
  background: var(--color-bg-secondary, #1e1e2e);
  border-bottom: 1px solid var(--color-border, #313244);
}

.snap-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.badge {
  padding: 0.2rem 0.5rem;
  border-radius: 4px;
  font-size: 0.75rem;
  font-weight: 600;
  background: var(--color-accent, #89b4fa);
  color: #1e1e2e;
}

.snap-id {
  font-family: monospace;
  font-size: 0.85rem;
  color: var(--color-text-muted, #6c7086);
}

.diff-summary {
  display: flex;
  gap: 1rem;
  padding: 0.5rem 1rem;
  font-size: 0.85rem;
  border-bottom: 1px solid var(--color-border, #313244);
}

.summary-item {
  font-weight: 600;
}
.summary-item.added { color: #a6e3a1; }
.summary-item.removed { color: #f38ba8; }
.summary-item.modified { color: #fab387; }
.summary-item.type-changed { color: #cba6f7; }

.filter-bar {
  display: flex;
  gap: 0.75rem;
  align-items: center;
  padding: 0.5rem 1rem;
  border-bottom: 1px solid var(--color-border, #313244);
  flex-wrap: wrap;
}

.filter-checkbox {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  font-size: 0.8rem;
  cursor: pointer;
}

.search-input {
  margin-left: auto;
  padding: 0.25rem 0.5rem;
  background: var(--color-bg-secondary, #1e1e2e);
  border: 1px solid var(--color-border, #313244);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  font-size: 0.85rem;
  min-width: 200px;
}

.diff-list {
  flex: 1;
  overflow-y: auto;
  padding: 0.5rem;
}

.diff-entry {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.3rem 0.5rem;
  border-radius: 4px;
  font-size: 0.85rem;
  font-family: monospace;
}

.diff-entry:hover {
  background: var(--color-bg-hover, #313244);
}

.diff-entry.added { color: #a6e3a1; }
.diff-entry.removed { color: #f38ba8; }
.diff-entry.modified { color: #fab387; }
.diff-entry.typeChanged { color: #cba6f7; }

.diff-status-icon {
  width: 1.2em;
  text-align: center;
  font-weight: 700;
}

.diff-path {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.diff-size, .diff-size-change {
  font-size: 0.75rem;
  color: var(--color-text-muted, #6c7086);
  white-space: nowrap;
}

.empty-state, .loading, .error {
  text-align: center;
  padding: 2rem;
  color: var(--color-text-muted, #6c7086);
}

.error { color: #f38ba8; }
</style>
