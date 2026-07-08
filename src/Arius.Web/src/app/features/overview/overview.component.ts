import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';
import { ApiService } from '../../core/api/api.service';
import { AccountDto } from '../../core/api/api-models';
import { DrawerStore } from '../../core/state/drawer.store';

/** Overview: KPI cards + the repositories table. */
@Component({
  selector: 'arius-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Header -->
    <div class="flex items-start justify-between">
      <div>
        <h1 class="ar-heading" style="font-size:22px;font-weight:700">Overview</h1>
        <p style="font-size:13.5px;color:#71717a;margin-top:2px">
          {{ repoCount() }} {{ repoCount() === 1 ? 'repository' : 'repositories' }} under management
        </p>
      </div>
      <div class="flex items-center gap-2.5">
        <button class="ar-btn-outline" (click)="refresh()"><i class="ki-filled ki-arrows-circle"></i>Refresh</button>
      </div>
    </div>

    <!-- KPI grid -->
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:18px;margin-top:22px">
      @for (kpi of kpis(); track kpi.label) {
        <div class="ar-card" data-testid="kpi-card" style="padding:19px 20px">
          <div class="flex items-center justify-between">
            <div style="width:42px;height:42px;border-radius:11px;display:flex;align-items:center;justify-content:center"
                 [style.background]="kpi.chipBg" [style.color]="kpi.chipFg">
              <i class="ki-filled {{ kpi.icon }}" style="font-size:20px"></i>
            </div>
          </div>
          <div style="font-size:25px;font-weight:700;color:#18181b;margin-top:12px;line-height:1">{{ kpi.value }}</div>
          <div style="font-size:13px;color:#71717a;margin-top:4px">{{ kpi.label }}</div>
        </div>
      }
    </div>

    <!-- Storage accounts table -->
    <div class="ar-card" style="margin-top:18px;padding:0;overflow:hidden">
      <div class="flex items-center justify-between" style="padding:18px 20px;border-bottom:1px solid #f0f0f2">
        <div>
          <div style="font-size:15.5px;font-weight:600;color:#18181b">Storage accounts</div>
          <div style="font-size:12.5px;color:#a1a1aa">Azure storage accounts under management</div>
        </div>
      </div>

      <div style="display:grid;grid-template-columns:2.4fr .7fr;padding:10px 20px;font-size:11px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">
        <div>Account</div><div>Repositories</div>
      </div>

      @if (accounts(); as list) {
        @for (account of list; track account.id) {
          <div class="ar-repo-row" data-testid="account-row" (click)="editAccount(account.id)"
               style="display:grid;grid-template-columns:2.4fr .7fr;align-items:center;padding:12px 20px;cursor:pointer;border-top:1px solid #f6f6f7">
            <div class="flex items-center gap-3">
              <div style="width:38px;height:38px;border-radius:10px;background:#f5f3ff;color:#6d28d9;display:flex;align-items:center;justify-content:center">
                <i class="ki-filled ki-cloud" style="font-size:18px"></i>
              </div>
              <div>
                <div class="ar-mono" style="font-size:14px;font-weight:600;color:#27272a">{{ account.name }}</div>
                <div style="font-size:12px;color:#a1a1aa">{{ account.hasKey ? 'Key configured' : 'No key' }}</div>
              </div>
            </div>
            <div style="font-size:12.5px;color:#71717a">{{ account.repositories }}</div>
          </div>
        } @empty {
          <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">
            No storage accounts yet — add one when creating a repository.
          </div>
        }
      } @else {
        <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">Loading…</div>
      }
    </div>

    <!-- Repositories table -->
    <div class="ar-card" style="margin-top:18px;padding:0;overflow:hidden">
      <div class="flex items-center justify-between" style="padding:18px 20px;border-bottom:1px solid #f0f0f2">
        <div>
          <div style="font-size:15.5px;font-weight:600;color:#18181b">Repositories</div>
          <div style="font-size:12.5px;color:#a1a1aa">Blob containers under management</div>
        </div>
        <div class="flex items-center gap-2.5">
          <button class="ar-btn-outline" data-testid="add-existing" (click)="go('/repos/add')"><i class="ki-filled ki-data"></i>Add existing</button>
          <button class="ar-btn-primary" data-testid="new-repository" (click)="go('/repos/create')"><i class="ki-filled ki-plus"></i>New repository</button>
        </div>
      </div>

      <div style="display:grid;grid-template-columns:2.4fr .9fr .9fr .7fr;padding:10px 20px;font-size:11px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:#a1a1aa">
        <div>Repository</div><div>Tier</div><div>Region</div><div>Account</div>
      </div>

      @if (repos(); as list) {
        @for (repo of list; track repo.id) {
          <div class="ar-repo-row" data-testid="repo-row" (click)="openRepo(repo.id)"
               style="display:grid;grid-template-columns:2.4fr .9fr .9fr .7fr;align-items:center;padding:12px 20px;cursor:pointer;border-top:1px solid #f6f6f7">
            <div class="flex items-center gap-3">
              <div style="width:38px;height:38px;border-radius:10px;background:#eff6ff;color:#3b82f6;display:flex;align-items:center;justify-content:center">
                <i class="ki-filled ki-folder" style="font-size:18px"></i>
              </div>
              <div>
                <div style="font-size:14px;font-weight:600;color:#27272a">{{ repo.alias }}</div>
                <div class="ar-mono" style="font-size:12px;color:#a1a1aa">{{ repo.container }}</div>
              </div>
            </div>
            <div><span style="font-size:12.5px;font-weight:600;text-transform:capitalize" [style.color]="tierColor(repo.defaultTier)">{{ repo.defaultTier }}</span></div>
            <div data-testid="repo-region" style="font-size:12.5px;color:#71717a">@if (repo.region) {{{ repo.region }}@if (repo.regionIsDefault) {<span style="color:#a1a1aa" title="Set the container's 'region' metadata for accurate pricing"> (default)</span>}} @else {<span style="color:#d4d4d8">—</span>}</div>
            <div class="ar-mono" style="font-size:12.5px;color:#71717a">{{ repo.account }}</div>
          </div>
        } @empty {
          <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">
            No repositories yet — add an existing one or create a new repository.
          </div>
        }
      } @else {
        <div style="padding:28px 20px;text-align:center;color:#a1a1aa;font-size:13px">Loading…</div>
      }
    </div>
  `,
})
export class OverviewComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly drawer = inject(DrawerStore);

  protected readonly repos = toSignal(this.api.listRepositories());
  protected readonly repoCount = computed(() => this.repos()?.length ?? 0);

  // Re-fetch whenever an account is added/edited/deleted via the wizard or the edit flyout.
  // catchError lives inside the switchMap so a failed fetch yields an empty list instead of killing the
  // outer revision stream — otherwise a single error would freeze the table on "Loading…" until a reload.
  protected readonly accounts = toSignal(
    toObservable(this.drawer.accountsRevision).pipe(
      switchMap(() => this.api.listAccounts().pipe(catchError(() => of([] as AccountDto[]))))));

  protected readonly kpis = computed(() => [
    { label: 'Repositories', value: String(this.repoCount()), icon: 'ki-folder', chipBg: '#eff6ff', chipFg: '#3b82f6' },
    { label: 'Total archived', value: '—', icon: 'ki-cloud', chipBg: '#f0fdf4', chipFg: '#15803d' },
    { label: 'Deduplicated', value: '—', icon: 'ki-data', chipBg: '#f5f3ff', chipFg: '#6d28d9' },
    { label: 'Est. monthly storage', value: '—', icon: 'ki-dollar', chipBg: '#fffbeb', chipFg: '#b45309' },
  ]);

  protected tierColor(tier: string): string {
    return tier?.toLowerCase() === 'hot' ? '#d97706'
      : tier?.toLowerCase() === 'cool' ? '#0ea5e9'
      : tier?.toLowerCase() === 'cold' ? '#3b82f6'
      : tier?.toLowerCase() === 'archive' ? '#8b5cf6' : '#a1a1aa';
  }

  protected refresh(): void { location.reload(); }
  protected go(path: string): void { this.router.navigateByUrl(path); }
  protected openRepo(id: number): void { this.router.navigate(['/repos', id, 'files']); }
  protected editAccount(id: number): void { this.drawer.openAccount(id); }
}
