<template>
  <RouterView />
</template>

<script setup lang="ts">
import { RouterView } from 'vue-router'
import { onMounted } from 'vue'
import { useSnapshotStore } from '@/stores/snapshotStore'

const snapshotStore = useSnapshotStore()

onMounted(() => {
  snapshotStore.loadSnapshots()
})
</script>

<style>
/* ── CSS reset ── */
*, *::before, *::after {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
}

/* ── Design tokens (Catppuccin Mocha-inspired dark theme) ── */
:root {
  --color-bg:           #181825;
  --color-bg-secondary: #1e1e2e;
  --color-bg-hover:     #262637;
  --color-bg-active:    #313244;
  --color-bg-tertiary:  #11111b;
  --color-border:       #313244;
  --color-text:         #cdd6f4;
  --color-text-muted:   #6c7086;
  --color-accent:       #89b4fa;
  --color-accent-dim:   #7287fd;
  --color-success:      #a6e3a1;
  --color-warning:      #fab387;
  --color-danger:       #f38ba8;
  --color-info:         #89dceb;

  color-scheme: dark;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
  font-size: 14px;
  line-height: 1.5;
}

/* Scrollbar styling */
::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}
::-webkit-scrollbar-track {
  background: var(--color-bg-secondary);
}
::-webkit-scrollbar-thumb {
  background: var(--color-border);
  border-radius: 3px;
}
::-webkit-scrollbar-thumb:hover {
  background: var(--color-text-muted);
}

/* ── Global element resets ── */
body {
  background: var(--color-bg);
  color: var(--color-text);
  height: 100vh;
  overflow: hidden;
}

#app {
  height: 100vh;
  display: flex;
  flex-direction: column;
}

button {
  font-family: inherit;
  font-size: inherit;
}

input, select, textarea {
  font-family: inherit;
  font-size: inherit;
  color: var(--color-text);
  background: var(--color-bg-secondary);
  border: 1px solid var(--color-border);
  border-radius: 4px;
  outline: none;
}

input:focus, select:focus, textarea:focus {
  border-color: var(--color-accent);
  box-shadow: 0 0 0 2px rgba(137, 180, 250, 0.2);
}
</style>
