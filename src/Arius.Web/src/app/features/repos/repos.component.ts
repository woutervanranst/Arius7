import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api/api.service';

/** Repositories list (icon-rail "Repos"). */
@Component({
  selector: 'arius-repos',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink],
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">Repositories</h1>
    <p style="font-size:13.5px;color:#71717a;margin-top:2px">Blob containers under management</p>

    <div class="ar-card" style="margin-top:22px;padding:0;overflow-x:auto">
      <div style="min-width:760px">
        <div style="display:grid;grid-template-columns:2.2fr .75fr .85fr .7fr 1.15fr;padding:10px 20px;font-size:11px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">
          <div>Repository</div><div>Tier</div><div>Region</div><div>Account</div><div>Last archive run</div>
        </div>
      @if (repos(); as list) {
        @for (repo of list; track repo.id) {
          <a class="ar-repo-row" [routerLink]="['/repos', repo.id, 'files']"
             style="display:grid;grid-template-columns:2.2fr .75fr .85fr .7fr 1.15fr;min-width:760px;align-items:center;padding:12px 20px;border-top:1px solid #f6f6f7;text-decoration:none">
            <div style="display:flex;align-items:center;gap:12px;min-width:0">
              <div style="width:38px;height:38px;flex:none;border-radius:10px;background:#eff6ff;color:#3b82f6;display:flex;align-items:center;justify-content:center">
                <i class="ki-filled ki-folder" style="font-size:18px"></i>
              </div>
              <div style="flex:1;min-width:0">
                <div style="font-size:14px;font-weight:600;color:#27272a">{{ repo.alias }}</div>
                <div class="ar-mono" style="font-size:12px;color:#a1a1aa">{{ repo.container }}</div>
              </div>
              <i class="ki-filled ki-right" style="flex:none;color:#d4d4d8"></i>
            </div>
            <div style="font-size:12.5px;font-weight:600;text-transform:capitalize;color:#71717a">{{ repo.defaultTier }}</div>
            <div data-testid="repo-region" style="font-size:12.5px;color:#71717a">
              @if (repo.region) {{{ repo.region }}@if (repo.regionIsDefault) {<span style="color:#a1a1aa" title="Set the container's 'region' metadata for accurate pricing"> (default)</span>}} @else {<span style="color:#d4d4d8">—</span>}
            </div>
            <span class="ar-mono" style="font-size:12.5px;color:#71717a">{{ repo.account }}</span>
            <span style="font-size:12.5px;color:#71717a;white-space:nowrap">
              @if (repo.lastArchiveStartedAt) { {{ repo.lastArchiveStartedAt | date:'dd MMM yyyy, HH:mm' }} }
              @else { <span style="color:#a1a1aa">Never</span> }
            </span>
          </a>
        } @empty {
          <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">No repositories yet.</div>
        }
      } @else {
        <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">Loading…</div>
      }
      </div>
    </div>
  `,
})
export class ReposComponent {
  private readonly api = inject(ApiService);
  protected readonly repos = toSignal(this.api.listRepositories());
}
