import { Injectable, inject, signal } from '@angular/core';
import { ApiService } from '../api/api.service';
import { RealtimeService } from '../api/realtime.service';
import { DefaultValues, OptionKey, ResolvedDefaults, archiveFlags, resolve, valuesOf } from '../defaults/defaults';
import { JobPillStore } from './job-pill.store';

export type DrawerType = 'archive' | 'restore' | 'properties' | 'account' | null;

/**
 * Drives the right slide-over drawers: Archive/Restore (a plain idle form — Start hands the job
 * straight to the floating pill and dismisses) and the Properties panel. A root singleton so the
 * repo header and the Files tab's "Restore collected" can open it from anywhere.
 */
@Injectable({ providedIn: 'root' })
export class DrawerStore {
  private readonly realtime = inject(RealtimeService);
  private readonly pill = inject(JobPillStore);
  private readonly api = inject(ApiService);
  private openSeq = 0;

  readonly type = signal<DrawerType>(null);
  readonly repoId = signal(0);
  readonly accountId = signal(0);
  readonly version = signal<string | null>(null);
  readonly collectedPaths = signal<string[]>([]);

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

  openRestore(repoId: number, version: string | null, collectedPaths: string[]): void {
    this.open('restore', repoId);
    this.version.set(version);
    this.collectedPaths.set(collectedPaths);
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
    const run = this.run();
    if (!run) return;
    this.error.set(null);
    const kind = this.type();
    let id: string;
    try {
      if (kind === 'archive') {
        id = await this.realtime.startArchive(this.repoId(), {
          tier: run.tier,
          // 'replace' (remove-local) requires pointers: the handler rejects removing a binary while writing none.
          ...archiveFlags(run.onDisk),
          fastHash: run.fastHash,
        });
      } else {
        id = await this.realtime.startRestore(this.repoId(), {
          version: this.version(), targetPaths: this.collectedPaths(), overwrite: run.overwrite, noPointers: run.noPointers,
        });
      }
    } catch (e) {
      // The hub rejects a second concurrent job on the same repo with a HubException; surface it
      // inline rather than leaving the drawer silently stuck on "Start".
      this.error.set(e instanceof Error ? e.message : String(e));
      return;
    }
    // Live progress from here on flows through the pill + detail page.
    this.pill.show(id, kind === 'restore' ? 'restore' : 'archive');
    this.type.set(null);
  }
}
