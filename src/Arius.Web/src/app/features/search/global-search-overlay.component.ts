import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SearchStore } from '../../core/state/search.store';
import { SearchHitDto } from '../../core/api/api-models';
import { StateRingComponent } from '../../shared/state-ring/state-ring.component';
import { formatBytes } from '../../shared/format';

/** Centered overlay for searching files across all repositories (⌘K). */
@Component({
  selector: 'arius-global-search',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, StateRingComponent],
  template: `
    @if (store.open()) {
      <div class="ar-search-scrim" (click)="store.close()"></div>
      <div class="ar-search-panel">
        <div class="flex items-center gap-3" style="padding:16px 18px;border-bottom:1px solid #f0f0f2">
          <i class="ki-filled ki-magnifier" style="font-size:18px;color:#a1a1aa"></i>
          <input #box autofocus [ngModel]="store.query()" (ngModelChange)="store.setQuery($event)"
                 placeholder="Search files across all repositories…" data-testid="search-input"
                 class="grow bg-transparent outline-none" style="font-size:15px" />
          <kbd style="font-size:11px;color:#a1a1aa;border:1px solid #e4e4e7;border-radius:6px;padding:2px 6px">Esc</kbd>
        </div>
        <div style="max-height:50vh;overflow-y:auto">
          @for (hit of store.results(); track hit.repoId + '|' + hit.entry.relativePath) {
            <button class="ar-hit" data-testid="search-result" (click)="openHit(hit)">
              <arius-state-ring [state]="hit.entry.state" [size]="19" />
              <i class="ki-outline ki-document" style="color:#a1a1aa;font-size:16px"></i>
              <div style="min-width:0;text-align:left">
                <div style="font-size:13.5px;color:#27272a;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{{ hit.entry.name }}</div>
                <div style="font-size:12px;color:#a1a1aa"><b style="color:#71717a">{{ hit.repo }}</b> · {{ hit.entry.relativePath }}</div>
              </div>
              <span style="margin-left:auto;font-size:12.5px;color:#71717a">{{ formatBytes(hit.entry.originalSize) }}</span>
            </button>
          } @empty {
            <div style="padding:24px;text-align:center;color:#a1a1aa;font-size:13px">
              {{ store.loading() ? 'Searching…' : (store.query() ? 'No matches.' : 'Type to search file names and paths.') }}
            </div>
          }
        </div>
        <div style="padding:10px 18px;border-top:1px solid #f0f0f2;font-size:11.5px;color:#a1a1aa">
          Searches file names and paths across every repository · backed by ListQuery filter
        </div>
      </div>
    }
  `,
  styles: [`
    .ar-search-scrim { position:fixed;inset:0;z-index:50;background:rgba(9,9,11,.22) }
    .ar-search-panel { position:fixed;z-index:51;top:72px;left:50%;transform:translateX(-50%);width:640px;max-width:92%;
      background:#fff;border:1px solid #e4e4e7;border-radius:14px;box-shadow:0 24px 60px rgba(9,9,11,.24);overflow:hidden }
    .ar-hit { display:flex;align-items:center;gap:11px;width:100%;padding:11px 18px;border-top:1px solid #f6f6f7 }
    .ar-hit:hover { background:#fafafb }
  `],
})
export class GlobalSearchOverlayComponent {
  protected readonly store = inject(SearchStore);
  private readonly router = inject(Router);
  protected readonly formatBytes = formatBytes;

  protected openHit(hit: SearchHitDto): void {
    this.store.close();
    // Carry the file's path so the Files tab opens the right repo, expands the tree
    // to the containing folder, and highlights the file.
    this.router.navigate(['/repos', hit.repoId, 'files'], {
      queryParams: { path: hit.entry.relativePath },
    });
  }
}
