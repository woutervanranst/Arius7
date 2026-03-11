<template>
  <div class="file-browser">
    <!-- Breadcrumb navigation -->
    <nav class="breadcrumb">
      <button class="crumb" @click="navigate('/')">root</button>
      <template v-for="(segment, i) in pathSegments" :key="i">
        <span class="crumb-sep">/</span>
        <button class="crumb" @click="navigate(segmentPath(i))">{{ segment }}</button>
      </template>
    </nav>

    <!-- Search bar -->
    <div class="browser-toolbar">
      <input
        v-model="searchPattern"
        placeholder="Search files (glob: *.txt)…"
        class="search-input"
        @keyup.enter="runSearch"
      />
      <button class="toolbar-btn" @click="runSearch">Find</button>
      <button
        v-if="selectedSnapshot"
        class="toolbar-btn primary"
        @click="emit('restore', selectedPath ?? currentPath)"
      >
        Restore here
      </button>
    </div>

    <!-- Sorting controls -->
    <div class="browser-cols">
      <button class="col-header sortable" @click="toggleSort('name')">
        Name {{ sortKey === 'name' ? (sortAsc ? '↑' : '↓') : '' }}
      </button>
      <button class="col-header sortable" @click="toggleSort('size')">
        Size {{ sortKey === 'size' ? (sortAsc ? '↑' : '↓') : '' }}
      </button>
      <button class="col-header sortable" @click="toggleSort('mTime')">
        Modified {{ sortKey === 'mTime' ? (sortAsc ? '↑' : '↓') : '' }}
      </button>
    </div>

    <!-- Loading / empty states -->
    <div v-if="loading" class="state-msg">Loading…</div>
    <div v-else-if="error" class="state-msg error">{{ error }}</div>
    <div v-else-if="!entries.length" class="state-msg">Empty directory</div>

    <!-- File rows -->
    <div class="file-list">
      <div
        v-for="entry in sortedEntries"
        :key="entry.path"
        class="file-row"
        :class="{ selected: selectedPath === entry.path }"
        @click="selectEntry(entry)"
        @dblclick="openEntry(entry)"
      >
        <span class="file-icon">{{ typeIcon(entry.type) }}</span>
        <span class="file-name">{{ entry.name }}</span>
        <span class="file-size">{{ formatSize(entry.size) }}</span>
        <span class="file-mtime">{{ formatDate(entry.mTime) }}</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import type { TreeEntry, TreeNodeType } from '@/types'
import { apiClient } from '@/services/api'
import { useSnapshotStore } from '@/stores/snapshotStore'

const emit = defineEmits<{
  restore: [path: string]
}>()

const snapshotStore = useSnapshotStore()
const selectedSnapshot = computed(() => snapshotStore.selectedSnapshot)

const currentPath = ref('/')
const entries = ref<TreeEntry[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const selectedPath = ref<string | null>(null)
const searchPattern = ref('')
const sortKey = ref<'name' | 'size' | 'mTime'>('name')
const sortAsc = ref(true)

const pathSegments = computed(() => {
  return currentPath.value.split('/').filter(Boolean)
})

function segmentPath(index: number): string {
  return '/' + pathSegments.value.slice(0, index + 1).join('/')
}

const sortedEntries = computed(() => {
  const list = [...entries.value]
  list.sort((a, b) => {
    // Dirs first
    if (a.type === 'dir' && b.type !== 'dir') return -1
    if (a.type !== 'dir' && b.type === 'dir') return 1
    let cmp = 0
    if (sortKey.value === 'name') cmp = a.name.localeCompare(b.name)
    else if (sortKey.value === 'size') cmp = a.size - b.size
    else cmp = a.mTime.localeCompare(b.mTime)
    return sortAsc.value ? cmp : -cmp
  })
  return list
})

function toggleSort(key: typeof sortKey.value) {
  if (sortKey.value === key) sortAsc.value = !sortAsc.value
  else { sortKey.value = key; sortAsc.value = true }
}

async function loadDirectory(path: string) {
  if (!selectedSnapshot.value) return
  loading.value = true
  error.value = null
  entries.value = []
  try {
    for await (const entry of apiClient.streamTree(selectedSnapshot.value.id.value, path, false)) {
      entries.value.push(entry)
    }
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function navigate(path: string) {
  currentPath.value = path
  selectedPath.value = null
}

function selectEntry(entry: TreeEntry) {
  selectedPath.value = entry.path
}

function openEntry(entry: TreeEntry) {
  if (entry.type === 'dir') {
    navigate(entry.path)
  }
}

async function runSearch() {
  if (!selectedSnapshot.value || !searchPattern.value.trim()) return
  loading.value = true
  error.value = null
  entries.value = []
  try {
    for await (const result of apiClient.streamFind(selectedSnapshot.value.id.value, searchPattern.value)) {
      entries.value.push({
        path: result.path,
        name: result.name,
        type: result.type,
        size: result.size,
        mTime: result.mTime,
        mode: result.mode,
      })
    }
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function typeIcon(type: TreeNodeType): string {
  if (type === 'dir') return '📁'
  if (type === 'symlink') return '🔗'
  return '📄'
}

function formatSize(bytes: number): string {
  if (bytes === 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = bytes
  let i = 0
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })
}

// Reload when snapshot changes
watch(
  () => snapshotStore.selectedId,
  () => {
    currentPath.value = '/'
    entries.value = []
    if (snapshotStore.selectedId) loadDirectory('/')
  }
)

// Reload when path changes
watch(currentPath, (path) => {
  if (snapshotStore.selectedId) loadDirectory(path)
})
</script>

<style scoped>
.file-browser {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  background: var(--color-main-bg, #181825);
}

.breadcrumb {
  display: flex;
  align-items: center;
  padding: 6px 12px;
  border-bottom: 1px solid var(--color-border, #313244);
  flex-wrap: wrap;
  gap: 2px;
}

.crumb {
  background: none;
  border: none;
  color: var(--color-accent, #89b4fa);
  cursor: pointer;
  font-size: 0.8rem;
  padding: 2px 4px;
  border-radius: 3px;
}

.crumb:hover { background: var(--color-hover, #313244); }

.crumb-sep {
  color: var(--color-text-muted, #a6adc8);
  font-size: 0.8rem;
}

.browser-toolbar {
  display: flex;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--color-border, #313244);
}

.search-input {
  flex: 1;
  background: var(--color-input-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  padding: 5px 10px;
  font-size: 0.82rem;
}

.toolbar-btn {
  background: var(--color-btn-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  cursor: pointer;
  padding: 5px 12px;
  font-size: 0.82rem;
}

.toolbar-btn.primary {
  background: var(--color-accent, #89b4fa);
  color: #1e1e2e;
  border-color: var(--color-accent, #89b4fa);
  font-weight: 600;
}

.browser-cols {
  display: grid;
  grid-template-columns: 1fr 100px 160px;
  border-bottom: 1px solid var(--color-border, #313244);
}

.col-header {
  background: none;
  border: none;
  color: var(--color-text-muted, #a6adc8);
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  padding: 5px 12px;
  text-align: left;
  cursor: pointer;
}

.col-header.sortable:hover { color: var(--color-text, #cdd6f4); }

.state-msg {
  padding: 20px;
  text-align: center;
  color: var(--color-text-muted, #a6adc8);
  font-size: 0.85rem;
}

.state-msg.error { color: var(--color-error, #f38ba8); }

.file-list { overflow-y: auto; flex: 1; }

.file-row {
  display: grid;
  grid-template-columns: 24px 1fr 100px 160px;
  align-items: center;
  padding: 4px 12px;
  cursor: pointer;
  font-size: 0.82rem;
  color: var(--color-text, #cdd6f4);
  transition: background 0.08s;
}

.file-row:hover { background: var(--color-hover, #313244); }
.file-row.selected { background: var(--color-selected, #45475a); }

.file-icon { font-size: 1rem; }
.file-name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; padding: 0 8px; }
.file-size { text-align: right; color: var(--color-text-muted, #a6adc8); padding-right: 8px; }
.file-mtime { color: var(--color-text-muted, #a6adc8); font-size: 0.78rem; }
</style>
