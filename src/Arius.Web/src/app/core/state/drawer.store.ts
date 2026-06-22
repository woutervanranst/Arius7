import { Injectable, inject, signal } from '@angular/core';
import { RealtimeService } from '../api/realtime.service';
import { CostEstimateMsg, LogLine } from '../api/api-models';

export type DrawerType = 'archive' | 'restore' | 'properties' | null;
export type StreamState = 'idle' | 'running' | 'cost' | 'done';

/**
 * Drives the right slide-over drawers: Archive/Restore (idle forms, live stream, cost-approval modal,
 * terminal state) and the Properties panel. A root singleton so the repo header and the Files tab's
 * "Restore collected" can open it from anywhere.
 */
@Injectable({ providedIn: 'root' })
export class DrawerStore {
  private readonly realtime = inject(RealtimeService);

  readonly type = signal<DrawerType>(null);
  readonly repoId = signal(0);
  readonly version = signal<string | null>(null);
  readonly collectedPaths = signal<string[]>([]);

  readonly streamState = signal<StreamState>('idle');
  readonly lines = signal<LogLine[]>([]);
  readonly progress = signal(0);
  readonly stats = signal<Record<string, string> | null>(null);
  readonly cost = signal<CostEstimateMsg | null>(null);
  readonly summary = signal('');
  readonly jobId = signal<string | null>(null);

  // Archive form
  readonly archiveTier = signal('archive');
  readonly removeLocal = signal(false);
  readonly noPointers = signal(false);
  // Restore form
  readonly overwrite = signal(false);
  readonly restoreNoPointers = signal(false);

  constructor() {
    this.realtime.log$.subscribe(line => { if (this.type()) this.lines.update(a => [...a.slice(-250), line]); });
    this.realtime.progress$.subscribe(p => { this.progress.set(p.pct); if (p.stats) this.stats.set(p.stats); });
    this.realtime.cost$.subscribe(c => { this.cost.set(c); this.streamState.set('cost'); });
    this.realtime.done$.subscribe(d => { this.streamState.set('done'); this.progress.set(100); this.summary.set(d.summary); });
  }

  openProperties(repoId: number): void {
    this.resetStream();
    this.type.set('properties');
    this.repoId.set(repoId);
  }

  openArchive(repoId: number, tier: string): void {
    this.resetStream();
    this.type.set('archive');
    this.repoId.set(repoId);
    this.archiveTier.set(tier || 'archive');
    this.removeLocal.set(false);
    this.noPointers.set(false);
  }

  openRestore(repoId: number, version: string | null, collectedPaths: string[]): void {
    this.resetStream();
    this.type.set('restore');
    this.repoId.set(repoId);
    this.version.set(version);
    this.collectedPaths.set(collectedPaths);
    this.overwrite.set(false);
    this.restoreNoPointers.set(false);
  }

  close(): void {
    // Closing while the cost modal is up cancels the parked restore (server treats null as decline).
    if (this.streamState() === 'cost') { const id = this.jobId(); if (id) void this.realtime.approve(id, null); }
    this.type.set(null);
    this.resetStream();
  }

  // Archive toggles are mutually exclusive (CLI: --remove-local vs --no-pointers).
  toggleRemoveLocal(): void {
    const next = !this.removeLocal();
    this.removeLocal.set(next);
    if (next) this.noPointers.set(false);
  }
  toggleNoPointers(): void {
    const next = !this.noPointers();
    this.noPointers.set(next);
    if (next) this.removeLocal.set(false);
  }

  async start(): Promise<void> {
    this.lines.set([]);
    this.progress.set(0);
    this.stats.set(null);
    this.cost.set(null);
    this.streamState.set('running');
    if (this.type() === 'archive') {
      this.jobId.set(await this.realtime.startArchive(this.repoId(), {
        tier: this.archiveTier(), removeLocal: this.removeLocal(), noPointers: this.noPointers(),
      }));
    } else {
      this.jobId.set(await this.realtime.startRestore(this.repoId(), {
        version: this.version(), targetPaths: this.collectedPaths(), overwrite: this.overwrite(), noPointers: this.restoreNoPointers(),
      }));
    }
  }

  async approve(priority: string | null): Promise<void> {
    this.streamState.set('running');
    const id = this.jobId();
    if (id) await this.realtime.approve(id, priority);
  }

  private resetStream(): void {
    this.streamState.set('idle');
    this.lines.set([]);
    this.progress.set(0);
    this.stats.set(null);
    this.cost.set(null);
    this.summary.set('');
    this.jobId.set(null);
  }
}
