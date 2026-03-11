<template>
  <div class="backup-trigger">
    <h3>Backup</h3>
    <p class="hint">Start a backup from server-side paths.</p>

    <div class="field">
      <label>Paths (one per line)</label>
      <textarea
        v-model="pathsText"
        class="paths-textarea"
        placeholder="/home/user/documents&#10;/var/data"
        rows="4"
      />
    </div>

    <div v-if="error" class="error-msg">{{ error }}</div>

    <button
      class="btn primary"
      :disabled="!canStart || running"
      @click="startBackup"
    >
      {{ running ? 'Starting…' : 'Start Backup' }}
    </button>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { apiClient } from '@/services/api'
import { useOperationStore } from '@/stores/operationStore'

const opStore = useOperationStore()
const pathsText = ref('')
const running = ref(false)
const error = ref<string | null>(null)

const canStart = computed(() => pathsText.value.trim().length > 0)

async function startBackup() {
  if (!canStart.value) return
  running.value = true
  error.value = null
  try {
    const paths = pathsText.value.split('\n').map(p => p.trim()).filter(Boolean)
    const { operationId } = await apiClient.startBackup({ paths })
    await opStore.track(operationId, 'backup')
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    running.value = false
  }
}
</script>

<style scoped>
.backup-trigger {
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

h3 { margin: 0; font-size: 1rem; color: var(--color-heading, #cdd6f4); }
.hint { margin: 0; font-size: 0.82rem; color: var(--color-text-muted, #a6adc8); }

.field { display: flex; flex-direction: column; gap: 4px; }
.field label { font-size: 0.78rem; color: var(--color-text-muted, #a6adc8); font-weight: 600; text-transform: uppercase; }

.paths-textarea {
  background: var(--color-input-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  padding: 8px 10px;
  font-size: 0.82rem;
  font-family: monospace;
  resize: vertical;
}

.error-msg { color: var(--color-error, #f38ba8); font-size: 0.82rem; }

.btn {
  align-self: flex-start;
  background: var(--color-btn-bg, #313244);
  border: 1px solid var(--color-border, #45475a);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  cursor: pointer;
  padding: 8px 18px;
  font-size: 0.85rem;
}

.btn.primary {
  background: var(--color-accent, #89b4fa);
  color: #1e1e2e;
  border-color: var(--color-accent, #89b4fa);
  font-weight: 600;
}

.btn:disabled { opacity: 0.5; cursor: not-allowed; }
</style>
