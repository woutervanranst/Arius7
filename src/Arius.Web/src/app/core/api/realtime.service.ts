import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import { CostEstimateMsg, DoneMsg, EntryDto, ListEntriesOptions, LogLine, ProgressMsg, SearchHitDto } from './api-models';

/**
 * SignalR client for Arius.Api's hub (/hubs/arius): file-browser entry streaming and the
 * archive/restore job streams (log/progress/cost/done) with the cost-approval handshake.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private connection?: signalR.HubConnection;
  private starting?: Promise<void>;
  private handlersBound = false;

  readonly log$ = new Subject<LogLine>();
  readonly progress$ = new Subject<ProgressMsg>();
  readonly cost$ = new Subject<CostEstimateMsg>();
  readonly done$ = new Subject<DoneMsg>();

  private ensureStarted(): Promise<void> {
    if (!this.connection) {
      this.connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/arius')
        .withAutomaticReconnect()
        .build();
    }
    if (!this.handlersBound) {
      const now = () => new Date().toLocaleTimeString('en-GB', { hour12: false });
      this.connection.on('Log', (m: { text: string; severity: LogLine['severity'] }) =>
        this.log$.next({ ts: now(), text: m.text, severity: m.severity }));
      this.connection.on('Progress', (m: ProgressMsg) => this.progress$.next(m));
      this.connection.on('CostEstimate', (m: CostEstimateMsg) => this.cost$.next(m));
      this.connection.on('Done', (m: DoneMsg) => this.done$.next(m));
      this.handlersBound = true;
    }
    if (this.connection.state === signalR.HubConnectionState.Connected) {
      return Promise.resolve();
    }
    // Cache the in-flight start, but drop it on failure so a later call can retry (API not up yet).
    this.starting ??= this.connection.start().catch(err => { this.starting = undefined; throw err; });
    return this.starting;
  }

  /** Starts an archive; returns the job id. Subscribe to log$/progress$/done$ for the stream. */
  async startArchive(repositoryId: number, opts: { tier: string; removeLocal: boolean; noPointers: boolean }): Promise<string> {
    await this.ensureStarted();
    return this.connection!.invoke<string>('StartArchive', repositoryId, opts.tier, opts.removeLocal, opts.noPointers);
  }

  /** Starts a restore (empty targetPaths = whole repository). Watch cost$ for the approval modal. */
  async startRestore(repositoryId: number, opts: { version: string | null; targetPaths: string[]; overwrite: boolean; noPointers: boolean }): Promise<string> {
    await this.ensureStarted();
    return this.connection!.invoke<string>('StartRestore', repositoryId, opts.version, opts.targetPaths, opts.overwrite, opts.noPointers);
  }

  /** Answers the restore cost modal. priority = 'standard' | 'high'; null/'' declines. */
  async approve(jobId: string, priority: string | null): Promise<void> {
    await this.ensureStarted();
    await this.connection!.invoke('Approve', jobId, priority);
  }

  /** Streams cross-repository search hits (filename filter across every repository). */
  searchAll(query: string): Observable<SearchHitDto> {
    return new Observable<SearchHitDto>(subscriber => {
      let stopped = false;
      let stream: signalR.ISubscription<SearchHitDto> | undefined;
      this.ensureStarted()
        .then(() => {
          if (stopped) return;
          stream = this.connection!.stream<SearchHitDto>('SearchAll', query).subscribe({
            next: hit => subscriber.next(hit),
            error: e => subscriber.error(e),
            complete: () => subscriber.complete(),
          });
        })
        .catch(e => subscriber.error(e));
      return () => { stopped = true; stream?.dispose(); };
    });
  }

  /** Streams the container names in an account (Add-existing wizard). */
  streamContainers(accountId: number, accountName: string | null, accountKey: string | null): Observable<string> {
    return new Observable<string>(subscriber => {
      let stopped = false;
      let stream: signalR.ISubscription<string> | undefined;
      this.ensureStarted()
        .then(() => {
          if (stopped) return;
          stream = this.connection!.stream<string>('StreamContainers', accountId, accountName, accountKey).subscribe({
            next: c => subscriber.next(c),
            error: e => subscriber.error(e),
            complete: () => subscriber.complete(),
          });
        })
        .catch(e => subscriber.error(e));
      return () => { stopped = true; stream?.dispose(); };
    });
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
