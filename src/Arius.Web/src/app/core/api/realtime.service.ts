import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Observable } from 'rxjs';
import { EntryDto, ListEntriesOptions } from './api-models';

/**
 * SignalR client for Arius.Api's hub (/hubs/arius). Phase 2 uses server→client streaming for the
 * file browser; archive/restore job streams + the cost-approval handshake are added later.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private connection?: signalR.HubConnection;
  private starting?: Promise<void>;

  private ensureStarted(): Promise<void> {
    if (!this.connection) {
      this.connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/arius')
        .withAutomaticReconnect()
        .build();
    }
    if (this.connection.state === signalR.HubConnectionState.Connected) {
      return Promise.resolve();
    }
    this.starting ??= this.connection.start();
    return this.starting;
  }

  /** Streams the immediate children of a folder in a snapshot. */
  listEntries(repositoryId: number, options: ListEntriesOptions = {}): Observable<EntryDto> {
    return new Observable<EntryDto>(subscriber => {
      let stopped = false;
      let stream: signalR.ISubscription<EntryDto> | undefined;

      this.ensureStarted()
        .then(() => {
          if (stopped) return;
          stream = this.connection!.stream<EntryDto>(
            'StreamEntries',
            repositoryId,
            options.version ?? null,
            options.prefix ?? null,
            options.filter ?? null,
            options.includeLocal ?? false,
          ).subscribe({
            next: entry => subscriber.next(entry),
            error: err => subscriber.error(err),
            complete: () => subscriber.complete(),
          });
        })
        .catch(err => subscriber.error(err));

      return () => {
        stopped = true;
        stream?.dispose();
      };
    });
  }
}
