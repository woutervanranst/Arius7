import { Injectable, inject, signal } from '@angular/core';
import { RealtimeService } from '../api/realtime.service';
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

  // Archive form
  readonly archiveTier = signal('archive');
  readonly archiveOnDisk = signal<'keep' | 'keep-pointers' | 'replace'>('keep');
  readonly fastHash = signal(false);
  // Restore form
  readonly overwrite = signal(false);
  readonly restoreNoPointers = signal(false);

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

  openArchive(repoId: number, tier: string): void {
    this.error.set(null);
    this.type.set('archive');
    this.repoId.set(repoId);
    this.archiveTier.set(tier || 'archive');
    this.archiveOnDisk.set('keep');
    this.fastHash.set(false);
  }

  openRestore(repoId: number, version: string | null, collectedPaths: string[]): void {
    this.error.set(null);
    this.type.set('restore');
    this.repoId.set(repoId);
    this.version.set(version);
    this.collectedPaths.set(collectedPaths);
    this.overwrite.set(false);
    this.restoreNoPointers.set(false);
  }

  close(): void {
    this.type.set(null);
  }

  async start(): Promise<void> {
    this.error.set(null);
    const kind = this.type();
    let id: string;
    try {
      if (kind === 'archive') {
        id = await this.realtime.startArchive(this.repoId(), {
          tier: this.archiveTier(),
          removeLocal: this.archiveOnDisk() === 'replace',
          // Both 'keep-pointers' and 'replace' write pointers; 'replace' (remove-local) requires them,
          // since the handler rejects removing a binary while writing no pointer.
          writePointers: this.archiveOnDisk() !== 'keep',
          fastHash: this.fastHash(),
        });
      } else {
        id = await this.realtime.startRestore(this.repoId(), {
          version: this.version(), targetPaths: this.collectedPaths(), overwrite: this.overwrite(), noPointers: this.restoreNoPointers(),
        });
      }
    } catch (e) {
      // The hub rejects a second concurrent job on the same repo with a HubException; surface it
      // inline rather than leaving the drawer silently stuck on "Start".
      this.error.set(e instanceof Error ? e.message : String(e));
      return;
    }
    // Hand off to the floating pill and dismiss the drawer (README §Interactions: "Start → drawer
    // dismisses → pill appears"). All live progress from here on flows through the pill + detail page.
    this.pill.show(id, kind === 'restore' ? 'restore' : 'archive');
    this.type.set(null);
  }
}
