<template>
  <div class="dialog-backdrop" @click.self="$emit('close')">
    <div class="dialog restore-dialog">
      <header class="dialog-header">
        <h3>Restore</h3>
        <button class="close-btn" @click="$emit('close')">✕</button>
      </header>

      <div class="dialog-body">
        <div class="field">
          <label>Snapshot</label>
          <div class="field-value">{{ snapshotLabel }}</div>
        </div>

        <div class="field" v-if="sourcePath">
          <label>Source path</label>
          <div class="field-value code">{{ sourcePath }}</div>
        </div>

        <div class="field">
          <label>Restore to</label>
          <input v-model="targetPath" class="text-input" placeholder="/path/to/restore" />
        </div>

        <div class="field">
          <label>Include filter (optional glob)</label>
          <input v-model="includeFilter" class="text-input" placeholder="e.g. *.pdf" />
        </div>

        <div class="field">
          <label>Priority</label>
          <select v-model="priority" class="text-input">
            <option value="standard">Standard (up to 15 hours rehydration)</option>
            <option value="high">High priority (up to 1 hour rehydration)</option>
          </select>
        </div>

        <div v-if="estimating" class="info-box">Estimating cost…</div>
        <div v-if="estimate" class="info-box">
          <strong>Cost estimate:</strong>
          {{ estimate.totalFiles.toLocaleString() }} files,
          {{ formatSize(estimate.totalBytes) }}
          (may require rehydration)
        </div>

        <div class="field checkbox-field">
          <label>
            <input v-model="confirmed" type="checkbox" />
            I understand this may trigger Azure rehydration charges
          </label>
        </div>
      </div>

      <footer class="dialog-footer">
        <button class="btn" @click="$emit('close')">Cancel</button>
        <button
          class="btn primary"
          :disabled="!canStart"
          @click="startRestore"
        >
          Start Restore
        </button>
      </footer>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import type { Snapshot } from '@/types'
import { apiClient } from '@/services/api'
import { useOperationStore } from '@/stores/operationStore'

const props = defineProps<{
  snapshot: Snapshot
  sourcePath?: string
}>()

const emit = defineEmits<{
  close: []
  started: [operationId: string]
}>()

const opStore = useOperationStore()

const targetPath = ref('')
const includeFilter = ref('')
const priority = ref<'standard' | 'high'>('standard')
const confirmed = ref(false)
const estimating = ref(false)
const estimate = ref<{ totalFiles: number; totalBytes: number } | null>(null)

const snapshotLabel = computed(() => {
  const s = props.snapshot
  return `${s.id.value.slice(0, 8)} — ${new Date(s.time).toLocaleString()}`
})

const canStart = computed(() =>
  !!targetPath.value.trim() && confirmed.value
)

async function startRestore() {
  if (!canStart.value) return
  const { operationId } = await apiClient.startRestore({
    snapshotId: props.snapshot.id.value,
    targetPath: targetPath.value,
    include: includeFilter.value || undefined,
  })
  await opStore.track(operationId, 'restore')
  emit('started', operationId)
  emit('close')
}

function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = bytes; let i = 0
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}
</script>

<style scoped>
.dialog-backdrop {
  position: fixed; inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
}

.dialog {
  background: var(--color-dialog-bg, #1e1e2e);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 8px;
  width: 480px;
  max-width: 95vw;
  display: flex;
  flex-direction: column;
  box-shadow: 0 8px 32px rgba(0,0,0,0.5);
}

.dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px 10px;
  border-bottom: 1px solid var(--color-border, #313244);
}

.dialog-header h3 { margin: 0; font-size: 1rem; color: var(--color-heading, #cdd6f4); }

.close-btn {
  background: none; border: none;
  color: var(--color-text-muted, #a6adc8);
  cursor: pointer; font-size: 1rem;
}

.dialog-body {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.field { display: flex; flex-direction: column; gap: 4px; }
.field label { font-size: 0.78rem; color: var(--color-text-muted, #a6adc8); font-weight: 600; text-transform: uppercase; }
.field-value { font-size: 0.85rem; color: var(--color-text, #cdd6f4); }
.field-value.code { font-family: monospace; background: var(--color-input-bg, #313244); padding: 4px 8px; border-radius: 4px; }

.text-input {
  background: var(--color-input-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  padding: 6px 10px;
  font-size: 0.85rem;
}

.checkbox-field label { display: flex; gap: 8px; align-items: center; text-transform: none; font-size: 0.82rem; }

.info-box {
  background: var(--color-info-bg, #313244);
  border-radius: 4px;
  padding: 8px 12px;
  font-size: 0.82rem;
  color: var(--color-text, #cdd6f4);
}

.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 16px;
  border-top: 1px solid var(--color-border, #313244);
}

.btn {
  background: var(--color-btn-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  cursor: pointer;
  padding: 7px 16px;
  font-size: 0.85rem;
}

.btn.primary {
  background: var(--color-accent, #89b4fa);
  color: #1e1e2e;
  border-color: var(--color-accent, #89b4fa);
  font-weight: 600;
}

.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
