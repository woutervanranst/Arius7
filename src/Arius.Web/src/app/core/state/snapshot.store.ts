import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../api/api.service';
import { SnapshotDto } from '../api/api-models';

/**
 * Holds the snapshot list and the selected snapshot for a repository. A root singleton so the
 * snapshot bar (rendered in the repo shell, above the tabs) and the Files tab share one selection.
 * `version` is null when the latest snapshot / live working state is selected.
 */
@Injectable({ providedIn: 'root' })
export class SnapshotStore {
  private readonly api = inject(ApiService);

  readonly snapshots = signal<SnapshotDto[]>([]);
  readonly version = signal<string | null>(null);

  private loadedRepo = -1;

  /** Active index into `snapshots` (0 = latest). Falls back to 0 when the version is unknown. */
  readonly activeIndex = computed(() => {
    const v = this.version();
    if (!v) return 0;
    return Math.max(0, this.snapshots().findIndex(s => s.version === v));
  });

  /** Load snapshots for a repo, resetting the selection to latest. No-op if already loaded. */
  load(repoId: number): void {
    if (repoId === this.loadedRepo) return;
    this.loadedRepo = repoId;
    this.version.set(null);
    this.api.getSnapshots(repoId).subscribe({
      next: s => this.snapshots.set(s),
      error: () => this.snapshots.set([]),
    });
  }

  /** Select a snapshot by index; index 0 (the latest) maps to null (live working state). */
  select(index: number): void {
    const list = this.snapshots();
    this.version.set(index === 0 ? null : (list[index]?.version ?? null));
  }
}
