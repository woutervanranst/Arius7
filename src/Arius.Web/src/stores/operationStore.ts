import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { BackupEvent, RestoreEvent, PruneEvent, ForgetEvent } from '@/types'
import { signalRService } from '@/services/signalr'
import { apiClient } from '@/services/api'

export type OperationKind = 'backup' | 'restore' | 'prune' | 'forget'
export type OperationStatus = 'running' | 'done' | 'error' | 'cancelled'

export interface Operation {
  id: string
  kind: OperationKind
  status: OperationStatus
  events: (BackupEvent | RestoreEvent | PruneEvent | ForgetEvent)[]
  error: string | null
}

export const useOperationStore = defineStore('operations', () => {
  const operations = ref<Map<string, Operation>>(new Map())
  const activeId = ref<string | null>(null)

  function createOperation(id: string, kind: OperationKind): Operation {
    const op: Operation = { id, kind, status: 'running', events: [], error: null }
    operations.value.set(id, op)
    activeId.value = id
    return op
  }

  function appendEvent(id: string, event: BackupEvent | RestoreEvent | PruneEvent | ForgetEvent) {
    operations.value.get(id)?.events.push(event)
  }

  function markDone(id: string) {
    const op = operations.value.get(id)
    if (op) op.status = 'done'
  }

  function markError(id: string, msg: string) {
    const op = operations.value.get(id)
    if (op) { op.status = 'error'; op.error = msg }
  }

  async function cancelOperation(id: string) {
    await apiClient.cancelOperation(id)
    const op = operations.value.get(id)
    if (op) op.status = 'cancelled'
  }

  /** Wire up SignalR listeners for an operation just started. */
  async function track(id: string, kind: OperationKind) {
    createOperation(id, kind)
    await signalRService.subscribeToOperation(id)

    const onBackup = (opId: string, ev: BackupEvent) => { if (opId === id) appendEvent(id, ev) }
    const onRestore = (opId: string, ev: RestoreEvent) => { if (opId === id) appendEvent(id, ev) }
    const onPrune = (opId: string, ev: PruneEvent) => { if (opId === id) appendEvent(id, ev) }
    const onForget = (opId: string, ev: ForgetEvent) => { if (opId === id) appendEvent(id, ev) }
    const onError = (opId: string, msg: string) => { if (opId === id) markError(id, msg) }

    signalRService.onBackupEvent(onBackup)
    signalRService.onRestoreEvent(onRestore)
    signalRService.onPruneEvent(onPrune)
    signalRService.onForgetEvent(onForget)
    signalRService.onError(onError)
  }

  return { operations, activeId, createOperation, appendEvent, markDone, markError, cancelOperation, track }
})
