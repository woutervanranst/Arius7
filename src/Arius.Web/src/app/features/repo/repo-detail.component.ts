import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { of, switchMap } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ApiService } from '../../core/api/api.service';
import { DrawerStore } from '../../core/state/drawer.store';
import { SnapshotBarComponent } from './snapshot-bar.component';

/** Repository detail shell: header (alias, chips, Properties/Restore/Archive) + snapshot bar + tab bar + child outlet. */
@Component({
  selector: 'arius-repo-detail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SnapshotBarComponent],
  template: `
    @if (repo(); as r) {
      <!-- Header -->
      <div class="flex items-start justify-between">
        <div class="flex items-start gap-3.5">
          <div style="width:48px;height:48px;border-radius:12px;background:#eff6ff;color:#3b82f6;display:flex;align-items:center;justify-content:center">
            <i class="ki-filled ki-folder" style="font-size:22px"></i>
          </div>
          <div>
            <h1 class="ar-heading" data-testid="repo-title" style="font-size:21px;font-weight:700">{{ r.alias }}</h1>
            <div class="flex items-center gap-2 flex-wrap" style="margin-top:7px">
              <span class="ar-chip"><i class="ki-filled ki-data"></i>{{ r.container }}</span>
              @if (r.localPath) { <span class="ar-chip"><i class="ki-filled ki-folder"></i>{{ r.localPath }}</span> }
              <span style="font-size:12.5px;color:#a1a1aa;text-transform:capitalize">{{ r.defaultTier }} tier · {{ r.account }}</span>
            </div>
          </div>
        </div>
        <div class="flex items-center gap-2.5">
          <button class="ar-btn-outline" data-testid="btn-properties" (click)="drawer.openProperties(r.id)"><i class="ki-filled ki-setting-2"></i>Properties</button>
          <button class="ar-btn-outline" data-testid="btn-restore" (click)="drawer.openRestore(r.id, null, [])"><i class="ki-filled ki-cloud-download"></i>Restore</button>
          <button class="ar-btn-primary" data-testid="btn-archive" (click)="drawer.openArchive(r.id, r.defaultTier)"><i class="ki-filled ki-cloud-add"></i>Archive</button>
        </div>
      </div>

      <!-- Snapshot time-travel bar (shared across tabs) -->
      <div style="margin-top:18px">
        <arius-snapshot-bar [repoId]="numericId()" />
      </div>

      <!-- Tab bar -->
      <div class="flex items-center gap-6" style="margin-top:18px;border-bottom:1px solid #f0f0f2">
        <a class="ar-tab" data-testid="tab-files" routerLink="files" routerLinkActive="active">Files</a>
        <a class="ar-tab" data-testid="tab-statistics" routerLink="statistics" routerLinkActive="active">Statistics</a>
      </div>

      <div style="margin-top:18px">
        <router-outlet></router-outlet>
      </div>
    } @else {
      <div style="padding:40px;text-align:center;color:#a1a1aa;font-size:13px">Loading repository…</div>
    }
  `,
  styles: [`
    .ar-chip {
      display: inline-flex; align-items: center; gap: 6px;
      font-family: var(--ar-font-mono); font-size: 12px; color: #52525b;
      background: #f7f7f8; border: 1px solid #ececef; border-radius: 7px; padding: 3px 9px;
    }
    .ar-chip i { font-size: 13px; color: #a1a1aa; }
  `],
})
export class RepoDetailComponent {
  private readonly api = inject(ApiService);
  protected readonly drawer = inject(DrawerStore);
  readonly repoId = input.required<string>();

  protected readonly repo = toSignal(
    toObservable(this.repoId).pipe(
      switchMap(id => this.api.getRepository(+id).pipe(catchError(() => of(null)))),
    ),
  );

  protected readonly numericId = computed(() => +this.repoId());
}
