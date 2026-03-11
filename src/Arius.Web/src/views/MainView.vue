<template>
  <div class="main-view">
    <!-- Header bar -->
    <header class="top-bar">
      <div class="logo">
        <span class="logo-icon">A</span>
        <span class="logo-text">Arius</span>
      </div>
      <nav class="nav-tabs">
        <button
          v-for="tab in navTabs"
          :key="tab.id"
          class="nav-tab"
          :class="{ active: activePane === tab.id }"
          @click="activePane = tab.id"
        >
          {{ tab.label }}
        </button>
      </nav>
      <div class="header-actions">
        <BackupTrigger />
      </div>
    </header>

    <!-- Main content area -->
    <div class="content-area">
      <!-- Snapshots + File Browser pane -->
      <template v-if="activePane === 'browse'">
        <aside class="sidebar">
          <SnapshotSidebar />
        </aside>
        <main class="main-panel">
          <FileBrowser
            v-if="snapshotStore.selectedId"
            :snapshot-id="snapshotStore.selectedId"
            @restore="(path) => openRestoreDialog(snapshotStore.selectedId!, path)"
          />
          <div v-else class="empty-state">
            Select a snapshot from the sidebar to browse its files.
          </div>
        </main>
      </template>

      <!-- Diff pane -->
      <template v-else-if="activePane === 'diff'">
        <div class="diff-panel">
          <div class="diff-selector">
            <label>
              From snapshot
              <select v-model="diffSnap1">
                <option value="">-- select --</option>
                <option v-for="s in allSnapshots" :key="s.id.value" :value="s.id.value">
                  {{ new Date(s.time).toLocaleString() }} ({{ s.id.value.slice(0, 8) }})
                </option>
              </select>
            </label>
            <label>
              To snapshot
              <select v-model="diffSnap2">
                <option value="">-- select --</option>
                <option v-for="s in allSnapshots" :key="s.id.value" :value="s.id.value">
                  {{ new Date(s.time).toLocaleString() }} ({{ s.id.value.slice(0, 8) }})
                </option>
              </select>
            </label>
          </div>
          <DiffView
            v-if="diffSnap1 && diffSnap2"
            :snap1-id="diffSnap1"
            :snap2-id="diffSnap2"
            :snap1-label="diffSnap1.slice(0, 12)"
            :snap2-label="diffSnap2.slice(0, 12)"
          />
          <div v-else class="empty-state">Select two snapshots to compare.</div>
        </div>
      </template>

      <!-- Stats pane -->
      <template v-else-if="activePane === 'stats'">
        <div class="stats-panel">
          <StatsDashboard />
        </div>
      </template>

      <!-- Forget/Prune pane -->
      <template v-else-if="activePane === 'manage'">
        <div class="manage-panel">
          <ForgetPruneManager />
        </div>
      </template>
    </div>

    <!-- Restore dialog (modal) -->
    <RestoreDialog
      v-if="restoreDialog.open && restoreDialog.snapshot"
      :snapshot="restoreDialog.snapshot"
      :source-path="restoreDialog.path || undefined"
      @close="restoreDialog.open = false"
      @started="restoreDialog.open = false"
    />

    <!-- Progress overlay for active operations -->
    <ProgressOverlay />
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import type { Snapshot } from '@/types'
import { useSnapshotStore } from '@/stores/snapshotStore'
import SnapshotSidebar from '@/components/SnapshotSidebar.vue'
import FileBrowser from '@/components/FileBrowser.vue'
import RestoreDialog from '@/components/RestoreDialog.vue'
import ProgressOverlay from '@/components/ProgressOverlay.vue'
import BackupTrigger from '@/components/BackupTrigger.vue'
import DiffView from '@/components/DiffView.vue'
import StatsDashboard from '@/components/StatsDashboard.vue'
import ForgetPruneManager from '@/components/ForgetPruneManager.vue'

type Pane = 'browse' | 'diff' | 'stats' | 'manage'

const snapshotStore = useSnapshotStore()
const activePane = ref<Pane>('browse')
const navTabs: { id: Pane; label: string }[] = [
  { id: 'browse', label: 'Browse' },
  { id: 'diff', label: 'Diff' },
  { id: 'stats', label: 'Stats' },
  { id: 'manage', label: 'Forget / Prune' },
]

const allSnapshots = computed(() => snapshotStore.snapshots)

// Diff selectors
const diffSnap1 = ref('')
const diffSnap2 = ref('')

// Restore dialog state
const restoreDialog = ref<{ open: boolean; snapshot: Snapshot | null; path: string }>({
  open: false,
  snapshot: null,
  path: '',
})

function openRestoreDialog(snapshotId: string, path: string) {
  const snapshot = snapshotStore.snapshots.find(s => s.id.value === snapshotId) ?? null
  restoreDialog.value = { open: true, snapshot, path }
}
</script>

<style scoped>
.main-view {
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
  background: var(--color-bg, #181825);
  color: var(--color-text, #cdd6f4);
}

/* Top bar */
.top-bar {
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 0 1rem;
  height: 52px;
  background: var(--color-bg-secondary, #1e1e2e);
  border-bottom: 1px solid var(--color-border, #313244);
  flex-shrink: 0;
}

.logo {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-right: 0.5rem;
}

.logo-icon {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  background: var(--color-accent, #89b4fa);
  color: #1e1e2e;
  font-weight: 800;
  font-size: 1rem;
  display: flex;
  align-items: center;
  justify-content: center;
}

.logo-text {
  font-weight: 700;
  font-size: 1.1rem;
  letter-spacing: 0.05em;
}

.nav-tabs {
  display: flex;
  gap: 0.25rem;
}

.nav-tab {
  padding: 0.3rem 0.9rem;
  background: transparent;
  border: none;
  border-radius: 5px;
  color: var(--color-text-muted, #6c7086);
  cursor: pointer;
  font-size: 0.875rem;
  font-weight: 500;
  transition: background 0.1s, color 0.1s;
}

.nav-tab:hover {
  background: var(--color-bg-hover, #313244);
  color: var(--color-text, #cdd6f4);
}

.nav-tab.active {
  background: var(--color-bg-active, #313244);
  color: var(--color-accent, #89b4fa);
}

.header-actions {
  margin-left: auto;
}

/* Content area */
.content-area {
  display: flex;
  flex: 1;
  overflow: hidden;
}

/* Sidebar */
.sidebar {
  width: 280px;
  min-width: 220px;
  max-width: 360px;
  border-right: 1px solid var(--color-border, #313244);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  resize: horizontal;
}

/* Main panels */
.main-panel,
.diff-panel,
.stats-panel,
.manage-panel {
  flex: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.diff-selector {
  display: flex;
  gap: 1rem;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid var(--color-border, #313244);
  flex-wrap: wrap;
}

.diff-selector label {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.8rem;
  color: var(--color-text-muted, #6c7086);
}

.diff-selector select {
  padding: 0.3rem 0.5rem;
  background: var(--color-bg-secondary, #1e1e2e);
  border: 1px solid var(--color-border, #313244);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  font-size: 0.85rem;
}

.empty-state {
  display: flex;
  align-items: center;
  justify-content: center;
  flex: 1;
  font-size: 0.9rem;
  color: var(--color-text-muted, #6c7086);
  text-align: center;
  padding: 2rem;
}

/* Responsive: collapse sidebar on narrow screens */
@media (max-width: 640px) {
  .sidebar {
    width: 100%;
    max-width: 100%;
    border-right: none;
    border-bottom: 1px solid var(--color-border, #313244);
  }

  .content-area {
    flex-direction: column;
  }

  .nav-tab {
    padding: 0.3rem 0.6rem;
    font-size: 0.8rem;
  }

  .top-bar {
    flex-wrap: wrap;
    height: auto;
    padding: 0.5rem;
  }
}
</style>
