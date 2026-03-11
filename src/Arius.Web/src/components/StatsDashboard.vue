<template>
  <div class="stats-dashboard">
    <div v-if="loading" class="loading">Loading statistics...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <template v-else-if="stats">
      <div class="stats-grid">
        <div class="stat-card">
          <div class="stat-label">Snapshots</div>
          <div class="stat-value">{{ stats.snapshotCount }}</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">Pack Files</div>
          <div class="stat-value">{{ stats.packCount }}</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">Unique Blobs</div>
          <div class="stat-value">{{ stats.uniqueBlobCount.toLocaleString() }}</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">Dedup Ratio</div>
          <div class="stat-value accent">{{ stats.deduplicationRatio.toFixed(2) }}x</div>
        </div>
      </div>

      <div class="storage-section">
        <h3 class="section-title">Storage Breakdown</h3>
        <div class="storage-row">
          <span class="storage-label">Total pack storage</span>
          <span class="storage-value">{{ formatBytes(stats.totalPackBytes) }}</span>
        </div>
        <div class="storage-row">
          <span class="storage-label">Unique data (pre-dedup)</span>
          <span class="storage-value">{{ formatBytes(stats.uniqueBlobBytes) }}</span>
        </div>
        <div class="storage-row highlight">
          <span class="storage-label">Space saved by deduplication</span>
          <span class="storage-value saved">{{ formatBytes(Math.max(0, stats.uniqueBlobBytes - stats.totalPackBytes)) }}</span>
        </div>
      </div>

      <div class="bar-section">
        <h3 class="section-title">Storage Efficiency</h3>
        <div class="bar-container">
          <div class="bar-label">Pack files</div>
          <div class="bar-track">
            <div class="bar-fill pack" :style="{ width: packBarWidth + '%' }"></div>
          </div>
          <div class="bar-bytes">{{ formatBytes(stats.totalPackBytes) }}</div>
        </div>
        <div class="bar-container">
          <div class="bar-label">Unique blobs</div>
          <div class="bar-track">
            <div class="bar-fill blob" :style="{ width: blobBarWidth + '%' }"></div>
          </div>
          <div class="bar-bytes">{{ formatBytes(stats.uniqueBlobBytes) }}</div>
        </div>
      </div>

      <div class="refresh-row">
        <button class="btn-secondary" @click="loadStats">Refresh</button>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import type { RepoStats } from '@/types'
import { apiClient } from '@/services/api'

const stats = ref<RepoStats | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

const maxBytes = computed(() => {
  if (!stats.value) return 1
  return Math.max(stats.value.totalPackBytes, stats.value.uniqueBlobBytes, 1)
})

const packBarWidth = computed(() => {
  if (!stats.value) return 0
  return (stats.value.totalPackBytes / maxBytes.value) * 100
})

const blobBarWidth = computed(() => {
  if (!stats.value) return 0
  return (stats.value.uniqueBlobBytes / maxBytes.value) * 100
})

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

async function loadStats() {
  loading.value = true
  error.value = null
  try {
    stats.value = await apiClient.getStats()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load statistics'
  } finally {
    loading.value = false
  }
}

onMounted(() => loadStats())
</script>

<style scoped>
.stats-dashboard {
  padding: 1rem;
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.loading, .error {
  text-align: center;
  padding: 2rem;
  color: var(--color-text-muted, #6c7086);
}

.error { color: #f38ba8; }

.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 0.75rem;
}

.stat-card {
  background: var(--color-bg-secondary, #1e1e2e);
  border: 1px solid var(--color-border, #313244);
  border-radius: 8px;
  padding: 1rem;
  text-align: center;
}

.stat-label {
  font-size: 0.75rem;
  color: var(--color-text-muted, #6c7086);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin-bottom: 0.5rem;
}

.stat-value {
  font-size: 1.75rem;
  font-weight: 700;
  color: var(--color-text, #cdd6f4);
}

.stat-value.accent {
  color: var(--color-accent, #89b4fa);
}

.section-title {
  font-size: 0.85rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--color-text-muted, #6c7086);
  margin-bottom: 0.75rem;
}

.storage-section {
  background: var(--color-bg-secondary, #1e1e2e);
  border: 1px solid var(--color-border, #313244);
  border-radius: 8px;
  padding: 1rem;
}

.storage-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0.4rem 0;
  font-size: 0.85rem;
  border-bottom: 1px solid var(--color-border, #313244);
}

.storage-row:last-child {
  border-bottom: none;
}

.storage-row.highlight {
  font-weight: 600;
}

.storage-label {
  color: var(--color-text-muted, #6c7086);
}

.storage-value {
  font-family: monospace;
  color: var(--color-text, #cdd6f4);
}

.storage-value.saved {
  color: #a6e3a1;
}

.bar-section {
  background: var(--color-bg-secondary, #1e1e2e);
  border: 1px solid var(--color-border, #313244);
  border-radius: 8px;
  padding: 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.bar-container {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  font-size: 0.8rem;
}

.bar-label {
  width: 110px;
  color: var(--color-text-muted, #6c7086);
  text-align: right;
  flex-shrink: 0;
}

.bar-track {
  flex: 1;
  height: 12px;
  background: var(--color-bg-tertiary, #181825);
  border-radius: 6px;
  overflow: hidden;
}

.bar-fill {
  height: 100%;
  border-radius: 6px;
  transition: width 0.4s ease;
}

.bar-fill.pack { background: #89b4fa; }
.bar-fill.blob { background: #a6e3a1; }

.bar-bytes {
  width: 80px;
  font-family: monospace;
  font-size: 0.75rem;
  color: var(--color-text-muted, #6c7086);
}

.refresh-row {
  display: flex;
  justify-content: flex-end;
}

.btn-secondary {
  padding: 0.4rem 1rem;
  background: transparent;
  border: 1px solid var(--color-border, #313244);
  border-radius: 4px;
  color: var(--color-text, #cdd6f4);
  cursor: pointer;
  font-size: 0.85rem;
}

.btn-secondary:hover {
  background: var(--color-bg-secondary, #1e1e2e);
}
</style>
