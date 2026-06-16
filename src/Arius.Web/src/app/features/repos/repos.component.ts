import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api/api.service';

/** Repositories list (icon-rail "Repos"). Click a row to open the repository. */
@Component({
  selector: 'arius-repos',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">Repositories</h1>
    <p style="font-size:13.5px;color:#71717a;margin-top:2px">Blob containers under management</p>

    <div class="ar-card" style="margin-top:22px;padding:0;overflow:hidden">
      @if (repos(); as list) {
        @for (repo of list; track repo.id) {
          <a class="ar-repo-row" [routerLink]="['/repos', repo.id, 'files']"
             style="display:flex;align-items:center;gap:12px;padding:13px 20px;border-top:1px solid #f6f6f7;text-decoration:none">
            <div style="width:38px;height:38px;border-radius:10px;background:#eff6ff;color:#3b82f6;display:flex;align-items:center;justify-content:center">
              <i class="ki-filled ki-folder" style="font-size:18px"></i>
            </div>
            <div style="flex:1">
              <div style="font-size:14px;font-weight:600;color:#27272a">{{ repo.alias }}</div>
              <div class="ar-mono" style="font-size:12px;color:#a1a1aa">{{ repo.container }}</div>
            </div>
            <span class="ar-mono" style="font-size:12.5px;color:#71717a">{{ repo.account }}</span>
            <i class="ki-filled ki-right" style="color:#d4d4d8"></i>
          </a>
        } @empty {
          <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">No repositories yet.</div>
        }
      } @else {
        <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">Loading…</div>
      }
    </div>
  `,
})
export class ReposComponent {
  private readonly api = inject(ApiService);
  protected readonly repos = toSignal(this.api.listRepositories());
}
