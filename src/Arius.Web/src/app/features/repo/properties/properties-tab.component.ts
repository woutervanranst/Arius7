import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { ApiService } from '../../../core/api/api.service';
import { RepositoryDto, ScheduleDto } from '../../../core/api/api-models';

/** Properties tab: friendly alias, read-only account/container, account key, encryption passphrase (rotate), local folder, schedules, and repository delete. */
@Component({
  selector: 'arius-properties-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe],
  template: `
    @if (repo(); as r) {
      <div class="ar-card" style="max-width:680px;padding:24px">
        <label class="ar-field">
          <span>Friendly alias</span>
          <input [(ngModel)]="alias" class="ar-input" data-testid="prop-alias" />
        </label>
        <div style="display:grid;grid-template-columns:1fr 1fr;gap:16px">
          <label class="ar-field"><span>Storage account</span><input class="ar-input ar-mono" [value]="r.account" readonly /></label>
          <label class="ar-field"><span>Container</span><input class="ar-input ar-mono" data-testid="prop-container" [value]="r.container" readonly /></label>
        </div>
        <label class="ar-field">
          <span>Account key</span>
          <input class="ar-input ar-mono" type="password" placeholder="•••••••• (stored encrypted — replace to rotate)" [(ngModel)]="accountKey" />
          <small>Stored encrypted in the Arius.Api SQLite. Replace to rotate the key.</small>
        </label>
        <label class="ar-field">
          <span>Encryption passphrase</span>
          <input class="ar-input ar-mono" type="password" data-testid="prop-passphrase" placeholder="•••••••• (replace to rotate)" [(ngModel)]="passphrase" />
          <small>Encrypts your data in the archive. Replace to rotate; existing snapshots stay readable with the old passphrase.</small>
        </label>
        @if (passphrase) {
          <label class="ar-field">
            <span>Confirm passphrase</span>
            <input class="ar-input ar-mono" type="password" data-testid="prop-passphrase-confirm" placeholder="Re-enter the new passphrase" [(ngModel)]="passphraseConfirm" />
            @if (passphraseMismatch()) { <small style="color:#dc2626">Passphrases don't match.</small> }
          </label>
        }
        <label class="ar-field">
          <span>Local folder</span>
          <input class="ar-input ar-mono" [(ngModel)]="localPath" />
          <small>Folder the Files view overlays against the archive, and the default source for archive runs.</small>
        </label>
        <div class="flex items-center justify-end gap-2.5" style="margin-top:18px">
          <button class="ar-btn-outline" (click)="reset(r)">Discard</button>
          <button class="ar-btn-primary" [disabled]="passphraseMismatch()" (click)="save()"><i class="ki-filled ki-check"></i>Save changes</button>
        </div>
        @if (saved()) { <div style="text-align:right;color:#15803d;font-size:12.5px;margin-top:8px">Saved.</div> }
        @if (saveError()) { <div data-testid="prop-save-error" style="text-align:right;color:#dc2626;font-size:12.5px;margin-top:8px">{{ saveError() }}</div> }
      </div>

      <!-- Scheduled archives -->
      <div class="ar-card" style="max-width:680px;padding:20px 24px;margin-top:18px">
        <div style="font-size:15.5px;font-weight:600;color:#18181b">Scheduled archives</div>
        <p style="font-size:12.5px;color:#a1a1aa;margin:2px 0 14px">Cron schedules fire archive runs automatically (UTC).</p>
        @for (s of schedules(); track s.id) {
          <div class="flex items-center gap-3" data-testid="schedule-row" style="padding:9px 0;border-top:1px solid #f4f4f5">
            <i class="ki-filled ki-calendar-tick" style="color:#6d28d9"></i>
            <span class="ar-mono" style="font-size:13px;color:#27272a">{{ s.cron }}</span>
            <span style="font-size:12px;color:#a1a1aa;margin-left:auto">{{ s.nextRun ? 'next ' + (s.nextRun | date:'dd MMM HH:mm') : 'computing…' }}</span>
            <button class="ar-icon-btn" data-testid="schedule-delete" (click)="removeSchedule(s.id)"><i class="ki-filled ki-trash"></i></button>
          </div>
        } @empty {
          <div style="font-size:13px;color:#a1a1aa;padding:6px 0">No schedules.</div>
        }
        <div class="flex items-center gap-2.5" style="margin-top:12px">
          <input class="ar-input ar-mono" data-testid="schedule-cron" style="flex:1" placeholder="cron e.g. 0 2 * * *" [(ngModel)]="newCron" />
          <button class="ar-btn-primary" data-testid="schedule-add" [disabled]="!newCron" (click)="addSchedule()"><i class="ki-filled ki-plus"></i>Add</button>
        </div>
      </div>

      <!-- Danger zone -->
      <div class="ar-card" style="max-width:680px;padding:20px 24px;margin-top:18px;border-color:#fecaca">
        <div style="font-size:15.5px;font-weight:600;color:#b91c1c">Delete repository</div>
        <p style="font-size:12.5px;color:#a1a1aa;margin:2px 0 14px">Removes <b>{{ r.alias }}</b> from Arius. The Azure container and its blobs are <b>not</b> deleted.</p>
        @if (confirmingDelete()) {
          <div class="flex items-center gap-2.5">
            <span style="font-size:13px;color:#b91c1c;margin-right:auto">Delete this repository from Arius?</span>
            <button class="ar-btn-outline" (click)="confirmingDelete.set(false)">Cancel</button>
            <button class="ar-btn-danger" data-testid="prop-delete-confirm" (click)="deleteRepository()"><i class="ki-filled ki-trash"></i>Confirm delete</button>
          </div>
        } @else {
          <button class="ar-btn-danger" data-testid="prop-delete" (click)="confirmingDelete.set(true)"><i class="ki-filled ki-trash"></i>Delete repository</button>
        }
        @if (deleteError()) { <div data-testid="prop-delete-error" style="color:#dc2626;font-size:12.5px;margin-top:10px">{{ deleteError() }}</div> }
      </div>
    } @else {
      <div style="padding:24px;color:#a1a1aa;font-size:13px">Loading…</div>
    }
  `,
  styles: [`
    .ar-field { display:block;margin-bottom:16px }
    .ar-field > span { display:block;font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:6px }
    .ar-field > small { display:block;font-size:11.5px;color:#a1a1aa;margin-top:5px }
    .ar-input { width:100%;height:40px;border:1px solid #e4e4e7;border-radius:9px;padding:0 12px;font-size:13.5px;color:#27272a;outline:none }
    .ar-input:focus { border-color:#3b82f6 }
    .ar-input[readonly] { background:#f7f7f8;color:#71717a }
    .ar-icon-btn { width:32px;height:32px;border-radius:8px;border:1px solid #e4e4e7;color:#a1a1aa;display:flex;align-items:center;justify-content:center }
    .ar-icon-btn:hover { color:#dc2626;border-color:#fecaca }
  `],
})
export class PropertiesTabComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  readonly repoId = input.required<string>();

  protected readonly repo = signal<RepositoryDto | null>(null);
  protected alias = '';
  protected accountKey = '';
  protected passphrase = '';
  protected passphraseConfirm = '';
  protected localPath = '';
  protected readonly saved = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly deleteError = signal<string | null>(null);
  protected readonly schedules = signal<ScheduleDto[]>([]);
  protected newCron = '';
  protected readonly confirmingDelete = signal(false);

  constructor() {
    // Reload when repoId changes — the router reuses this component across /repos/:id navigations.
    effect(onCleanup => {
      const id = +this.repoId();
      this.repo.set(null);
      const repoSub = this.api.getRepository(id).subscribe(r => { this.repo.set(r); this.reset(r); });
      const schedulesSub = this.api.getSchedules(id).subscribe(s => this.schedules.set(s));
      onCleanup(() => { repoSub.unsubscribe(); schedulesSub.unsubscribe(); });   // drop in-flight requests if repoId changes first
    });
  }

  private loadSchedules(): void {
    this.api.getSchedules(+this.repoId()).subscribe(s => this.schedules.set(s));
  }

  protected addSchedule(): void {
    this.api.createSchedule(+this.repoId(), this.newCron).subscribe(() => { this.newCron = ''; this.loadSchedules(); });
  }

  protected removeSchedule(id: number): void {
    this.api.deleteSchedule(+this.repoId(), id).subscribe(() => this.loadSchedules());
  }

  protected deleteRepository(): void {
    this.deleteError.set(null);
    this.api.deleteRepository(+this.repoId()).subscribe({
      next: () => this.router.navigate(['/overview']),
      // Stay on the page (confirm button still shown) so the user can retry.
      error: () => this.deleteError.set('Could not delete the repository — please try again.'),
    });
  }

  /** True once a new passphrase is typed but the confirmation doesn't match — blocks Save. */
  protected passphraseMismatch(): boolean {
    return this.passphrase !== '' && this.passphrase !== this.passphraseConfirm;
  }

  protected reset(r: RepositoryDto): void {
    this.alias = r.alias;
    this.localPath = r.localPath ?? '';
    this.accountKey = '';
    this.passphrase = '';
    this.passphraseConfirm = '';
    this.saved.set(false);
    this.saveError.set(null);
  }

  protected save(): void {
    this.saveError.set(null);
    // Send the passphrase only when a new one is entered; omitting it leaves the stored one unchanged.
    this.api.patchRepository(+this.repoId(), {
      alias: this.alias,
      localPath: this.localPath,
      ...(this.passphrase ? { passphrase: this.passphrase } : {}),
    }).subscribe({
      next: r => {
        this.repo.set(r);
        this.passphrase = '';
        this.passphraseConfirm = '';
        this.saved.set(true);
      },
      // Keep the entered passphrase/fields intact so the user can retry.
      error: () => this.saveError.set('Could not save changes — please try again.'),
    });
  }
}
