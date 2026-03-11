import * as signalR from '@microsoft/signalr'
import type { BackupEvent, RestoreEvent, PruneEvent, ForgetEvent } from '@/types'

export type OperationEventHandler<T> = (operationId: string, event: T) => void
export type ErrorHandler = (operationId: string, message: string) => void

let connection: signalR.HubConnection | null = null

function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub/operations')
      .withAutomaticReconnect()
      .build()
  }
  return connection
}

export const signalRService = {
  async connect(): Promise<void> {
    const conn = getConnection()
    if (conn.state === signalR.HubConnectionState.Disconnected) {
      await conn.start()
    }
  },

  async disconnect(): Promise<void> {
    if (connection && connection.state !== signalR.HubConnectionState.Disconnected) {
      await connection.stop()
    }
  },

  async subscribeToOperation(operationId: string): Promise<void> {
    await this.connect()
    await getConnection().invoke('Subscribe', operationId)
  },

  async unsubscribeFromOperation(operationId: string): Promise<void> {
    if (connection) {
      await connection.invoke('Unsubscribe', operationId)
    }
  },

  onBackupEvent(handler: OperationEventHandler<BackupEvent>): void {
    getConnection().on('BackupEvent', handler)
  },

  onRestoreEvent(handler: OperationEventHandler<RestoreEvent>): void {
    getConnection().on('RestoreEvent', handler)
  },

  onPruneEvent(handler: OperationEventHandler<PruneEvent>): void {
    getConnection().on('PruneEvent', handler)
  },

  onForgetEvent(handler: OperationEventHandler<ForgetEvent>): void {
    getConnection().on('ForgetEvent', handler)
  },

  onError(handler: ErrorHandler): void {
    getConnection().on('Error', handler)
  },

  offBackupEvent(handler: OperationEventHandler<BackupEvent>): void {
    getConnection().off('BackupEvent', handler)
  },

  offRestoreEvent(handler: OperationEventHandler<RestoreEvent>): void {
    getConnection().off('RestoreEvent', handler)
  },

  offPruneEvent(handler: OperationEventHandler<PruneEvent>): void {
    getConnection().off('PruneEvent', handler)
  },

  offForgetEvent(handler: OperationEventHandler<ForgetEvent>): void {
    getConnection().off('ForgetEvent', handler)
  },

  get connectionState(): signalR.HubConnectionState {
    return connection?.state ?? signalR.HubConnectionState.Disconnected
  },
}
