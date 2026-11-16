import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { DefaultValues, OptionKey, ResolvedDefaults, archiveFlags, resolve, valuesOf } from '../defaults/defaults';
import { JobPillStore } from './job-pill.store';
import { SnapshotStore } from './snapshot.store';

/** A file the user ticked in the Files tab; `inRepository` is false for local-only files, which a restore/download cannot target. */
export interface CollectedFile { path: string; size: number; inRepository: boolean }

export type DrawerType = 'archive' | 'restore' | 'download' | 'properties' | 'account' | null;

/**
 * Drives the right slide-over drawers: Archive/Restore (a plain idle form — Start hands the job
 * straight to the floating pill and dismisses) and the Properties panel. A root singleton so the
 * repo header and the Files tab's "Restore" and "Download" can open it from anywhere.
 */
@Injectable({ providedIn: 'root' })
export class DrawerStore {
  private readonly realtime = inject(RealtimeService);
  private readonly pill = inject(JobPillStore);
  private readonly api = inject(ApiService);
  private readonly snapshots = inject(SnapshotStore);
  private openSeq = 0;

  readonly type = signal<DrawerType>(null);
  readonly repoId = signal(0);
  readonly accountId = signal(0);
  readonly version = signal<string | null>(null);
  readonly collected = signal<CollectedFile[]>([]);
  /** The collected files a restore/download can act on; local-only files are left out. */
  readonly targets = computed(() => this.collected().filter(f => f.inRepository));
  readonly skippedCount = computed(() => this.collected().length - this.targets().length);
  readonly targetBytes = computed(() => this.targets().reduce((sum, f) => sum + f.size, 0));
  /** False when every collected file is local-only, so Start never degrades into a whole-repository job. */
  readonly canStart = computed(() => {
    switch (this.type()) {
      case 'download': return this.targets().length > 0;
      case 'restore': return this.run() !== null && (this.collected().length === 0 || this.targets().length > 0);
      case 'archive': return this.run() !== null;
      default: return false;
    }
  });

  /** Bumped whenever an account is created/edited/deleted, so account lists (e.g. Overview) can re-fetch. */
  readonly accountsRevision = signal(0);
  bumpAccounts(): void { this.accountsRevision.update(n => n + 1); }

  /** Set when StartArchive/StartRestore rejects (e.g. the busy-repo HubException); surfaced by the drawer. */
  readonly error = signal<string | null>(null);

  /** The repository's resolved defaults (with provenance), and this run's values — null until they load. */
  readonly resolved = signal<ResolvedDefaults | null>(null);
  readonly run = signal<DefaultValues | null>(null);

  openProperties(repoId: number): void {
    this.error.set(null);
    this.type.set('properties');
    this.repoId.set(repoId);
  }

  openAccount(accountId: number): void {
    this.error.set(null);
    this.type.set('account');
    this.accountId.set(accountId);
  }

  openArchive(repoId: number): void {
    this.open('archive', repoId);
  }

  openRestore(repoId: number, version: string | null, collected: CollectedFile[]): void {
    this.open('restore', repoId);
    this.version.set(version);
    this.collected.set(collected);
  }

  /** A download has no options, so unlike restore it loads no defaults. */
  openDownload(repoId: number, version: string | null, collected: CollectedFile[]): void {
    this.openSeq++;   // drop a still-pending defaults response from a previous open
    this.error.set(null);
    this.type.set('download');
    this.repoId.set(repoId);
    this.resolved.set(null);
    this.run.set(null);
    this.version.set(version);
    this.collected.set(collected);
  }

  pick(key: OptionKey, value: DefaultValues[OptionKey]): void {
    this.run.update(run => run && { ...run, [key]: value });
  }

  resetOption(key: OptionKey): void {
    const resolved = this.resolved();
    if (resolved) this.pick(key, resolved[key].value);
  }

  /** Opens the drawer and pre-fills the run from the repository's resolved defaults; a response for a superseded open is dropped. */
  private open(type: 'archive' | 'restore', repoId: number): void {
    this.error.set(null);
    this.type.set(type);
    this.repoId.set(repoId);
    this.resolved.set(null);
    this.run.set(null);
    const seq = ++this.openSeq;
    this.api.getRepositoryDefaults(repoId).subscribe({
      next: dto => {
        if (seq !== this.openSeq) return;
        const resolved = resolve(dto.set, dto.inherited, 'repository');
        this.resolved.set(resolved);
        this.run.set(valuesOf(resolved));
      },
      error: () => { if (seq === this.openSeq) this.error.set('Could not load this repository\'s defaults.'); },
    });
  }

  close(): void {
    this.type.set(null);
  }

  async start(): Promise<void> {
    if (!this.canStart()) return;
    const run = this.run()!;
    this.error.set(null);
    const kind = this.type();
    let id: string;
    try {
      if (kind === 'download') {
        id = await this.realtime.startDownload(this.repoId(), {
          // Pin to the newest snapshot when none is selected, so a snapshot published mid-job can't change what is fetched.
          version: this.version() ?? this.snapshots.snapshots().at(-1)?.version ?? '',
          files: this.targets().map(({ path, size }) => ({ path, size })),
        });
      } else if (kind === 'archive') {
        id = await this.realtime.startArchive(this.repoId(), {
          tier: run.tier,
          // 'replace' (remove-local) requires pointers: the handler rejects removing a binary while writing none.
          ...archiveFlags(run.onDisk),
          fastHash: run.fastHash,
        });
      } else {
        id = await this.realtime.startRestore(this.repoId(), {
          version: this.version(), targetPaths: this.targets().map(t => t.path), overwrite: run.overwrite, noPointers: run.noPointers,
        });
      }
    } catch (e) {
      // The hub rejects a second concurrent job on the same repo with a HubException; surface it
      // inline rather than leaving the drawer silently stuck on "Start".
      this.error.set(e instanceof Error ? e.message : String(e));
      return;
    }
    // Live progress from here on flows through the pill + detail page.
    this.pill.show(id, kind === 'download' || kind === 'restore' ? kind : 'archive');
    this.type.set(null);
  }
}
