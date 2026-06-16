import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api/api.service';
import { RepositoryDto } from '../../../core/api/api-models';

/** Properties tab: friendly alias, read-only account/container, account key (rotate), local folder. Save in a later phase. */
@Component({
  selector: 'arius-properties-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    @if (repo(); as r) {
      <div class="ar-card" style="max-width:680px;padding:24px">
        <label class="ar-field">
          <span>Friendly alias</span>
          <input [(ngModel)]="alias" class="ar-input" />
        </label>
        <div style="display:grid;grid-template-columns:1fr 1fr;gap:16px">
          <label class="ar-field"><span>Storage account</span><input class="ar-input ar-mono" [value]="r.account" readonly /></label>
          <label class="ar-field"><span>Container</span><input class="ar-input ar-mono" [value]="r.container" readonly /></label>
        </div>
        <label class="ar-field">
          <span>Account key</span>
          <input class="ar-input ar-mono" type="password" placeholder="•••••••• (stored encrypted — replace to rotate)" [(ngModel)]="accountKey" />
          <small>Stored encrypted in the Arius.Api SQLite. Replace to rotate the key.</small>
        </label>
        <label class="ar-field">
          <span>Local folder</span>
          <input class="ar-input ar-mono" [(ngModel)]="localPath" />
          <small>Folder the Files view overlays against the archive, and the default source for archive runs.</small>
        </label>
        <div class="flex items-center justify-end gap-2.5" style="margin-top:18px">
          <button class="ar-btn-outline" (click)="reset(r)">Discard</button>
          <button class="ar-btn-primary" (click)="save()"><i class="ki-filled ki-check"></i>Save changes</button>
        </div>
        @if (saved()) { <div style="text-align:right;color:#15803d;font-size:12.5px;margin-top:8px">Saved.</div> }
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
  `],
})
export class PropertiesTabComponent {
  private readonly api = inject(ApiService);
  readonly repoId = input.required<string>();

  protected readonly repo = signal<RepositoryDto | null>(null);
  protected alias = '';
  protected accountKey = '';
  protected localPath = '';
  protected readonly saved = signal(false);

  constructor() {
    queueMicrotask(() => {
      this.api.getRepository(+this.repoId()).subscribe(r => { this.repo.set(r); this.reset(r); });
    });
  }

  protected reset(r: RepositoryDto): void {
    this.alias = r.alias;
    this.localPath = r.localPath ?? '';
    this.accountKey = '';
    this.saved.set(false);
  }

  protected save(): void {
    // Phase 2 saves alias + local folder; account-key rotation is account-level (later phase).
    this.api.patchRepository(+this.repoId(), {
      alias: this.alias,
      localPath: this.localPath,
    }).subscribe(r => { this.repo.set(r); this.saved.set(true); });
  }
}
