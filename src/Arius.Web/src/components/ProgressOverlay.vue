<template>
  <div v-if="activeOperation" class="progress-overlay">
    <div class="progress-card">
      <header class="progress-header">
        <h3>{{ kindLabel }} in progress</h3>
        <button class="cancel-btn" @click="cancel">Cancel</button>
      </header>

      <!-- Generic progress bar -->
      <div v-if="percent !== null" class="progress-bar-wrap">
        <div class="progress-bar" :style="{ width: percent + '%' }"></div>
      </div>

      <!-- Backup progress -->
      <template v-if="activeOperation.kind === 'backup'">
        <div class="stat-row">
          <span>Files: {{ processedFiles }} / {{ totalFiles }}</span>
          <span v-if="dedupedFiles">Deduped: {{ dedupedFiles }}</span>
        </div>
      </template>

      <!-- Restore progress -->
      <template v-if="activeOperation.kind === 'restore'">
        <div class="stat-row">
          <span>Files: {{ processedFiles }} / {{ totalFiles }}</span>
          <span>{{ formatSize(processedBytes) }} / {{ formatSize(totalBytes) }}</span>
        </div>
      </template>

      <!-- Prune/Forget progress -->
      <template v-if="activeOperation.kind === 'prune' || activeOperation.kind === 'forget'">
        <div class="stat-row">
          <span>{{ latestMessage }}</span>
        </div>
      </template>

      <!-- Scrollable event log -->
      <div class="event-log" ref="logEl">
        <div
          v-for="(ev, i) in activeOperation.events.slice(-50)"
          :key="i"
          class="log-line"
        >
          {{ formatEvent(ev) }}
        </div>
      </div>

      <div v-if="activeOperation.status === 'done'" class="done-msg">Completed!</div>
      <div v-if="activeOperation.status === 'error'" class="error-msg">{{ activeOperation.error }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch, nextTick } from 'vue'
import { useOperationStore } from '@/stores/operationStore'
import type { BackupEvent, RestoreEvent, PruneEvent, ForgetEvent } from '@/types'

const opStore = useOperationStore()
const logEl = ref<HTMLElement | null>(null)

const activeOperation = computed(() => {
  if (!opStore.activeId) return null
  return opStore.operations.get(opStore.activeId) ?? null
})

const kindLabel = computed(() => {
  const k = activeOperation.value?.kind
  if (k === 'backup') return 'Backup'
  if (k === 'restore') return 'Restore'
  if (k === 'prune') return 'Prune'
  if (k === 'forget') return 'Forget'
  return 'Operation'
})

// Backup derived
const totalFiles = computed(() => {
  const op = activeOperation.value
  if (!op) return 0
  const started = op.events.find((e): e is { $type: 'BackupStarted'; totalFiles: number } =>
    '$type' in e && (e as BackupEvent).$type === 'BackupStarted'
  )
  return started ? started.totalFiles : 0
})

const processedFiles = computed(() => {
  const op = activeOperation.value
  if (!op) return 0
  return op.events.filter((e): boolean => '$type' in e && (e as BackupEvent).$type === 'BackupFileProcessed').length
})

const dedupedFiles = computed(() => {
  const op = activeOperation.value
  if (!op) return 0
  return op.events.filter((e): boolean => {
    if (!('$type' in e)) return false
    const ev = e as BackupEvent
    return ev.$type === 'BackupFileProcessed' && ev.isDeduplicated
  }).length
})

// Restore derived
const totalBytes = computed(() => {
  const op = activeOperation.value
  if (!op) return 0
  const plan = op.events.find((e): e is { $type: 'RestorePlanReady'; totalFiles: number; totalBytes: number } =>
    '$type' in e && (e as RestoreEvent).$type === 'RestorePlanReady'
  )
  return plan ? plan.totalBytes : 0
})

const processedBytes = computed(() => {
  const op = activeOperation.value
  if (!op) return 0
  return op.events
    .filter((e): boolean => '$type' in e && (e as RestoreEvent).$type === 'RestoreFileRestored')
    .reduce((sum, e) => sum + ((e as { size: number }).size ?? 0), 0)
})

const percent = computed(() => {
  const op = activeOperation.value
  if (!op) return null
  if (op.kind === 'backup' && totalFiles.value > 0)
    return Math.round((processedFiles.value / totalFiles.value) * 100)
  if (op.kind === 'restore' && totalBytes.value > 0)
    return Math.round((processedBytes.value / totalBytes.value) * 100)
  return null
})

const latestMessage = computed(() => {
  const op = activeOperation.value
  if (!op) return ''
  const last = [...op.events].reverse().find((e) => 'message' in e) as PruneEvent | ForgetEvent | undefined
  if (!last) return ''
  if ('message' in last) return (last as PruneEvent).message
  if ('reason' in last) return `${(last as ForgetEvent).snapshotId.slice(0, 8)}: ${(last as ForgetEvent).decision}`
  return ''
})

function formatEvent(ev: BackupEvent | RestoreEvent | PruneEvent | ForgetEvent): string {
  if ('$type' in ev) {
    const be = ev as BackupEvent
    if (be.$type === 'BackupFileProcessed') return `${be.isDeduplicated ? '[dedup]' : '[new]  '} ${be.path}`
    if (be.$type === 'BackupStarted') return `Backup started (${be.totalFiles} files)`
    if (be.$type === 'BackupCompleted') return `Backup complete — snapshot ${be.snapshot.id.value.slice(0, 8)}`
    const re = ev as RestoreEvent
    if (re.$type === 'RestoreFileRestored') return `Restored: ${re.path}`
    if (re.$type === 'RestorePlanReady') return `Plan: ${re.totalFiles} files (${formatSize(re.totalBytes)})`
    if (re.$type === 'RestoreCompleted') return `Restore complete — ${re.restoredFiles} files`
  }
  if ('kind' in ev) return `[${(ev as PruneEvent).kind}] ${(ev as PruneEvent).message}`
  if ('decision' in ev) return `[${(ev as ForgetEvent).decision}] ${(ev as ForgetEvent).snapshotId.slice(0, 8)} — ${(ev as ForgetEvent).reason}`
  return JSON.stringify(ev)
}

function formatSize(bytes: number): string {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = bytes; let i = 0
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

async function cancel() {
  if (opStore.activeId) await opStore.cancelOperation(opStore.activeId)
}

// Auto-scroll log
watch(
  () => activeOperation.value?.events.length,
  async () => {
    await nextTick()
    if (logEl.value) logEl.value.scrollTop = logEl.value.scrollHeight
  }
)
</script>

<style scoped>
.progress-overlay {
  position: fixed; bottom: 24px; right: 24px;
  z-index: 200;
  width: 440px;
  max-width: 95vw;
}

.progress-card {
  background: var(--color-dialog-bg, #1e1e2e);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 8px;
  box-shadow: 0 4px 24px rgba(0,0,0,0.5);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.progress-header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 10px 14px;
  border-bottom: 1px solid var(--color-border, #313244);
}

.progress-header h3 { margin: 0; font-size: 0.9rem; color: var(--color-heading, #cdd6f4); }

.cancel-btn {
  background: none; border: none;
  color: var(--color-text-muted, #a6adc8);
  cursor: pointer; font-size: 0.8rem;
  padding: 2px 8px;
  border-radius: 4px;
  border: 1px solid transparent;
}
.cancel-btn:hover { border-color: var(--color-error, #f38ba8); color: var(--color-error, #f38ba8); }

.progress-bar-wrap {
  height: 4px;
  background: var(--color-border, #313244);
}

.progress-bar {
  height: 100%;
  background: var(--color-accent, #89b4fa);
  transition: width 0.3s ease;
}

.stat-row {
  display: flex; justify-content: space-between;
  padding: 6px 14px;
  font-size: 0.78rem;
  color: var(--color-text-muted, #a6adc8);
}

.event-log {
  max-height: 180px;
  overflow-y: auto;
  padding: 4px 14px;
  font-size: 0.72rem;
  font-family: monospace;
  color: var(--color-text, #cdd6f4);
  background: var(--color-main-bg, #181825);
  border-top: 1px solid var(--color-border, #313244);
}

.log-line { padding: 1px 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

.done-msg {
  padding: 8px 14px;
  color: var(--color-success, #a6e3a1);
  font-size: 0.82rem;
  font-weight: 600;
  border-top: 1px solid var(--color-border, #313244);
}

.error-msg {
  padding: 8px 14px;
  color: var(--color-error, #f38ba8);
  font-size: 0.82rem;
  border-top: 1px solid var(--color-border, #313244);
}
</style>
