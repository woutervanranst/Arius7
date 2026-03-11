<template>
  <div class="forget-prune-manager">
    <div class="tabs">
      <button
        v-for="tab in tabs"
        :key="tab.id"
        class="tab-btn"
        :class="{ active: activeTab === tab.id }"
        @click="activeTab = tab.id"
      >
        {{ tab.label }}
      </button>
    </div>

    <!-- Forget Tab -->
    <div v-if="activeTab === 'forget'" class="tab-content">
      <h3 class="section-title">Retention Policy</h3>
      <div class="policy-grid">
        <label class="policy-row">
          <span>Keep last N snapshots</span>
          <input v-model.number="policy.keepLast" type="number" min="0" placeholder="any" />
        </label>
        <label class="policy-row">
          <span>Keep hourly</span>
          <input v-model.number="policy.keepHourly" type="number" min="0" placeholder="any" />
        </label>
        <label class="policy-row">
          <span>Keep daily</span>
          <input v-model.number="policy.keepDaily" type="number" min="0" placeholder="any" />
        </label>
        <label class="policy-row">
          <span>Keep weekly</span>
          <input v-model.number="policy.keepWeekly" type="number" min="0" placeholder="any" />
        </label>
        <label class="policy-row">
          <span>Keep monthly</span>
          <input v-model.number="policy.keepMonthly" type="number" min="0" placeholder="any" />
        </label>
        <label class="policy-row">
          <span>Keep yearly</span>
          <input v-model.number="policy.keepYearly" type="number" min="0" placeholder="any" />
        </label>
        <label class="policy-row">
          <span>Keep within</span>
          <input v-model="policy.keepWithin" type="text" placeholder="e.g. 7d, 1m" />
        </label>
      </div>

      <div class="dry-run-toggle">
        <label class="toggle-label">
          <input type="checkbox" v-model="forgetDryRun" />
          Dry run (preview only, no changes)
        </label>
      </div>

      <button class="btn-primary" @click="runForget" :disabled="forgetRunning">
        {{ forgetDryRun ? 'Preview Forget' : 'Run Forget' }}
      </button>

      <!-- Forget preview results -->
      <div v-if="forgetResults.length > 0" class="results-section">
        <h4 class="results-title">
          Preview — {{ forgetResults.filter(r => r.decision === 'remove').length }} snapshots to remove,
          {{ forgetResults.filter(r => r.decision === 'keep').length }} to keep
        </h4>
        <div class="results-list">
          <div
            v-for="r in forgetResults"
            :key="r.snapshotId"
            class="result-row"
            :class="r.decision"
          >
            <span class="result-decision">{{ r.decision === 'keep' ? '✓ keep' : '✗ remove' }}</span>
            <span class="result-id">{{ r.snapshotId.slice(0, 12) }}…</span>
            <span class="result-time">{{ new Date(r.snapshotTime).toLocaleString() }}</span>
            <span class="result-reason">{{ r.reason }}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Prune Tab -->
    <div v-if="activeTab === 'prune'" class="tab-content">
      <p class="description">
        Prune removes data blobs that are no longer referenced by any snapshot.
        Run forget first to mark snapshots for removal.
      </p>

      <div class="dry-run-toggle">
        <label class="toggle-label">
          <input type="checkbox" v-model="pruneDryRun" />
          Dry run (preview only, no changes)
        </label>
      </div>

      <button class="btn-primary" @click="runPrune" :disabled="pruneRunning">
        {{ pruneDryRun ? 'Preview Prune' : 'Run Prune' }}
      </button>

      <div v-if="pruneEvents.length > 0" class="results-section">
        <h4 class="results-title">Prune log</h4>
        <div class="prune-log">
          <div v-for="(evt, i) in pruneEvents" :key="i" class="prune-log-row">
            <span class="prune-kind" :class="evt.kind">{{ evt.kind }}</span>
            <span class="prune-msg">{{ evt.message }}</span>
            <span v-if="evt.bytesAffected" class="prune-bytes">{{ formatBytes(evt.bytesAffected) }}</span>
          </div>
        </div>
      </div>
    </div>

    <div v-if="operationError" class="error-msg">{{ operationError }}</div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive } from 'vue'
import type { RetentionPolicy, ForgetEvent, PruneEvent } from '@/types'
import { apiClient } from '@/services/api'
import { useOperationStore } from '@/stores/operationStore'

const activeTab = ref<'forget' | 'prune'>('forget')
const tabs = [
  { id: 'forget' as const, label: 'Forget' },
  { id: 'prune' as const, label: 'Prune' },
]

const policy = reactive<Partial<RetentionPolicy>>({
  keepLast: undefined,
  keepHourly: undefined,
  keepDaily: undefined,
  keepWeekly: undefined,
  keepMonthly: undefined,
  keepYearly: undefined,
  keepWithin: undefined,
  keepTags: undefined,
})

const forgetDryRun = ref(true)
const pruneDryRun = ref(true)
const forgetRunning = ref(false)
const pruneRunning = ref(false)
const forgetResults = ref<ForgetEvent[]>([])
const pruneEvents = ref<PruneEvent[]>([])
const operationError = ref<string | null>(null)

const operationStore = useOperationStore()

async function runForget() {
  forgetRunning.value = true
  operationError.value = null
  forgetResults.value = []
  try {
    const builtPolicy: RetentionPolicy = {
      keepLast: policy.keepLast ?? null,
      keepHourly: policy.keepHourly ?? null,
      keepDaily: policy.keepDaily ?? null,
      keepWeekly: policy.keepWeekly ?? null,
      keepMonthly: policy.keepMonthly ?? null,
      keepYearly: policy.keepYearly ?? null,
      keepWithin: policy.keepWithin ?? null,
      keepTags: policy.keepTags ?? null,
    }
    const { operationId } = await apiClient.startForget({ policy: builtPolicy, dryRun: forgetDryRun.value })
    await operationStore.track(operationId, 'forget')
    // results are streamed via SignalR into the operation store events
    const op = operationStore.operations.get(operationId)
    if (op) forgetResults.value = op.events.filter(e => 'decision' in e) as ForgetEvent[]
  } catch (e) {
    operationError.value = e instanceof Error ? e.message : 'Forget failed'
  } finally {
    forgetRunning.value = false
  }
}

async function runPrune() {
  pruneRunning.value = true
  operationError.value = null
  pruneEvents.value = []
  try {
    const { operationId } = await apiClient.startPrune({ dryRun: pruneDryRun.value })
    await operationStore.track(operationId, 'prune')
    const op = operationStore.operations.get(operationId)
    if (op) pruneEvents.value = op.events.filter(e => 'kind' in e) as PruneEvent[]
  } catch (e) {
    operationError.value = e instanceof Error ? e.message : 'Prune failed'
  } finally {
    pruneRunning.value = false
  }
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}
</script>

<style scoped>
.forget-prune-manager {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.tabs {
  display: flex;
  border-bottom: 1px solid var(--color-border, #313244);
}

.tab-btn {
  padding: 0.6rem 1.25rem;
  background: transparent;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--color-text-muted, #6c7086);
  cursor: pointer;
  font-size: 0.9rem;
  font-weight: 500;
  transition: color 0.15s, border-color 0.15s;
}

.tab-btn.active,
.tab-btn:hover {
  color: var(--color-text, #cdd6f4);
}

.tab-btn.active {
  border-bottom-color: var(--color-accent, #89b4fa);
}

.tab-content {
  flex: 1;
  overflow-y: auto;
  padding: 1rem;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.section-title {
  font-size: 0.85rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--color-text-muted, #6c7086);
}

.policy-grid {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.policy-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 0.85rem;
  gap: 1rem;
}

.policy-row span {
  color: var(--color-text-muted, #6c7086);
}

.policy-row input {
  width: 100px;
  padding: 0.25rem 0.5rem;
  background: var(--color-bg-secondary, #1e1e2e);
  border: 1px solid var(--color-border, #313244);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  font-size: 0.85rem;
  text-align: right;
}

.dry-run-toggle {
  font-size: 0.85rem;
}

.toggle-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
  color: var(--color-text, #cdd6f4);
}

.btn-primary {
  padding: 0.5rem 1.25rem;
  background: var(--color-accent, #89b4fa);
  border: none;
  border-radius: 6px;
  color: #1e1e2e;
  font-weight: 600;
  cursor: pointer;
  font-size: 0.9rem;
  align-self: flex-start;
}

.btn-primary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn-primary:hover:not(:disabled) {
  filter: brightness(1.1);
}

.description {
  font-size: 0.85rem;
  color: var(--color-text-muted, #6c7086);
  line-height: 1.5;
}

.results-section {
  margin-top: 0.5rem;
}

.results-title {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--color-text-muted, #6c7086);
  margin-bottom: 0.5rem;
}

.results-list {
  border: 1px solid var(--color-border, #313244);
  border-radius: 6px;
  overflow: hidden;
}

.result-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.35rem 0.75rem;
  font-size: 0.8rem;
  border-bottom: 1px solid var(--color-border, #313244);
}

.result-row:last-child { border-bottom: none; }
.result-row.keep { background: rgba(166, 227, 161, 0.05); }
.result-row.remove { background: rgba(243, 139, 168, 0.07); }

.result-decision {
  width: 70px;
  font-weight: 600;
}
.result-row.keep .result-decision { color: #a6e3a1; }
.result-row.remove .result-decision { color: #f38ba8; }

.result-id {
  font-family: monospace;
  color: var(--color-text-muted, #6c7086);
}

.result-time {
  color: var(--color-text-muted, #6c7086);
}

.result-reason {
  flex: 1;
  color: var(--color-text, #cdd6f4);
}

.prune-log {
  border: 1px solid var(--color-border, #313244);
  border-radius: 6px;
  overflow: hidden;
  max-height: 300px;
  overflow-y: auto;
}

.prune-log-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.3rem 0.75rem;
  font-size: 0.8rem;
  border-bottom: 1px solid var(--color-border, #313244);
  font-family: monospace;
}

.prune-log-row:last-child { border-bottom: none; }

.prune-kind {
  width: 80px;
  font-weight: 600;
  text-transform: uppercase;
}

.prune-kind.analysing { color: #89b4fa; }
.prune-kind.willDelete { color: #f38ba8; }
.prune-kind.willRepack { color: #fab387; }
.prune-kind.deleting { color: #f38ba8; }
.prune-kind.repacking { color: #fab387; }
.prune-kind.done { color: #a6e3a1; }

.prune-msg { flex: 1; color: var(--color-text, #cdd6f4); }
.prune-bytes { color: var(--color-text-muted, #6c7086); }

.error-msg {
  padding: 0.75rem 1rem;
  background: rgba(243, 139, 168, 0.1);
  border: 1px solid #f38ba8;
  border-radius: 6px;
  color: #f38ba8;
  font-size: 0.85rem;
  margin: 0.5rem 1rem;
}
</style>
