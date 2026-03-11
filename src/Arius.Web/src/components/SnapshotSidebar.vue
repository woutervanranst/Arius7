<template>
  <aside class="snapshot-sidebar">
    <div class="sidebar-header">
      <h2>Snapshots</h2>
      <button class="refresh-btn" :disabled="snapshotStore.loading" @click="snapshotStore.loadSnapshots()">
        {{ snapshotStore.loading ? '...' : '↻' }}
      </button>
    </div>

    <div v-if="snapshotStore.error" class="error-msg">{{ snapshotStore.error }}</div>

    <!-- Tag filter -->
    <div class="filter-row">
      <input
        v-model="tagFilter"
        placeholder="Filter by tag"
        class="filter-input"
      />
      <input
        v-model="hostFilter"
        placeholder="Filter by host"
        class="filter-input"
      />
    </div>

    <div v-if="snapshotStore.loading && !snapshotStore.snapshots.length" class="loading">Loading…</div>

    <div v-for="group in filteredGroups" :key="group.label" class="snap-group">
      <div class="snap-group-label">{{ group.label }}</div>
      <button
        v-for="snap in group.snapshots"
        :key="snap.id.value"
        class="snap-item"
        :class="{ active: snap.id.value === snapshotStore.selectedId }"
        @click="snapshotStore.selectSnapshot(snap.id.value)"
      >
        <span class="snap-time">{{ formatTime(snap.time) }}</span>
        <span class="snap-host">{{ snap.hostname }}</span>
        <span v-if="snap.tags.length" class="snap-tags">{{ snap.tags.join(', ') }}</span>
      </button>
    </div>

    <div v-if="!snapshotStore.loading && !filteredGroups.length" class="empty">
      No snapshots found.
    </div>
  </aside>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useSnapshotStore } from '@/stores/snapshotStore'

const snapshotStore = useSnapshotStore()
const tagFilter = ref('')
const hostFilter = ref('')

const filteredGroups = computed(() => {
  const tag = tagFilter.value.toLowerCase()
  const host = hostFilter.value.toLowerCase()
  return snapshotStore.groups
    .map(g => ({
      ...g,
      snapshots: g.snapshots.filter(s => {
        const matchTag = !tag || s.tags.some(t => t.toLowerCase().includes(tag))
        const matchHost = !host || s.hostname.toLowerCase().includes(host)
        return matchTag && matchHost
      }),
    }))
    .filter(g => g.snapshots.length > 0)
})

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

onMounted(() => {
  if (!snapshotStore.snapshots.length) {
    snapshotStore.loadSnapshots()
  }
})
</script>

<style scoped>
.snapshot-sidebar {
  width: 260px;
  min-width: 200px;
  background: var(--color-sidebar-bg, #1e1e2e);
  border-right: 1px solid var(--color-border, #313244);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.sidebar-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--color-border, #313244);
}

.sidebar-header h2 {
  margin: 0;
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--color-heading, #cdd6f4);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.refresh-btn {
  background: none;
  border: none;
  cursor: pointer;
  color: var(--color-text-muted, #a6adc8);
  font-size: 1.1rem;
  padding: 2px 6px;
}

.filter-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--color-border, #313244);
}

.filter-input {
  background: var(--color-input-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  padding: 4px 8px;
  font-size: 0.75rem;
  width: 100%;
  box-sizing: border-box;
}

.loading, .empty {
  padding: 16px;
  text-align: center;
  color: var(--color-text-muted, #a6adc8);
  font-size: 0.85rem;
}

.snap-group {
  overflow-y: auto;
}

.snap-group-label {
  padding: 6px 12px 2px;
  font-size: 0.7rem;
  color: var(--color-text-muted, #a6adc8);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  position: sticky;
  top: 0;
  background: var(--color-sidebar-bg, #1e1e2e);
}

.snap-item {
  display: flex;
  flex-direction: column;
  width: 100%;
  text-align: left;
  background: none;
  border: none;
  cursor: pointer;
  padding: 6px 16px;
  border-left: 3px solid transparent;
  color: var(--color-text, #cdd6f4);
  font-size: 0.8rem;
  transition: background 0.1s;
}

.snap-item:hover {
  background: var(--color-hover, #313244);
}

.snap-item.active {
  border-left-color: var(--color-accent, #89b4fa);
  background: var(--color-hover, #313244);
}

.snap-time { font-weight: 600; }
.snap-host { color: var(--color-text-muted, #a6adc8); font-size: 0.75rem; }
.snap-tags { color: var(--color-accent, #89b4fa); font-size: 0.7rem; }

.error-msg {
  color: var(--color-error, #f38ba8);
  font-size: 0.8rem;
  padding: 8px 12px;
}
</style>
