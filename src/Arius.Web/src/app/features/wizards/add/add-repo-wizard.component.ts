import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toArray } from 'rxjs/operators';
import { ApiService } from '../../../core/api/api.service';
import { RealtimeService } from '../../../core/api/realtime.service';

/** Add-existing repository: 2-step wizard (storage account → container + details). */
@Component({
  selector: 'arius-add-wizard',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    <div style="max-width:620px;margin:0 auto">
      <div style="font-size:13px;color:#71717a">Step <b>{{ step() }}</b> of 2 · {{ step() === 1 ? 'Storage account' : 'Repository' }}</div>
      <div style="display:flex;gap:6px;margin:8px 0 22px">
        <div style="flex:1;height:4px;border-radius:999px;background:#3b82f6"></div>
        <div style="flex:1;height:4px;border-radius:999px" [style.background]="step() === 2 ? '#3b82f6' : '#e4e4e7'"></div>
      </div>

      @if (step() === 1) {
        <h1 class="ar-heading" style="font-size:20px;font-weight:700;margin-bottom:16px">Storage account</h1>
        <div class="ar-seg" style="margin-bottom:18px">
          <button [class.on]="mode() === 'select'" (click)="mode.set('select')">Use configured</button>
          <button [class.on]="mode() === 'new'" (click)="mode.set('new')">Add new account</button>
        </div>

        @if (mode() === 'select') {
          @for (a of accounts(); track a.id) {
            <label class="ar-radio" [class.on]="selectedAccountId() === a.id" (click)="selectedAccountId.set(a.id)">
              <span class="ar-dot"></span>
              <span class="ar-mono" style="font-weight:600">{{ a.name }}</span>
              <span style="color:#a1a1aa;margin-left:auto">{{ a.repositories }} repositories</span>
            </label>
          } @empty {
            <div style="color:#a1a1aa;font-size:13px">No configured accounts — add a new one.</div>
          }
        } @else {
          <label class="ar-field"><span>Account name</span><input class="ar-input ar-mono" [(ngModel)]="newName" /></label>
          <label class="ar-field"><span>Account key</span><input class="ar-input ar-mono" type="password" [(ngModel)]="newKey" /></label>
        }

        <div class="ar-note" style="margin-top:6px">Arius reads the existing manifest and snapshots — no files are uploaded.</div>
        @if (error()) { <div style="color:#dc2626;font-size:12.5px;margin-top:8px">{{ error() }}</div> }
        <div class="flex items-center justify-end gap-2.5" style="margin-top:22px">
          <button class="ar-btn-outline" (click)="cancel()">Cancel</button>
          <button class="ar-btn-primary" [disabled]="discovering()" (click)="discover()">
            <i class="ki-filled ki-arrow-right"></i>{{ discovering() ? 'Discovering…' : 'Connect & discover' }}
          </button>
        </div>
      } @else {
        <h1 class="ar-heading" style="font-size:20px;font-weight:700;margin-bottom:16px">Repository</h1>
        <div class="ar-field"><span>Select container</span>
          @for (c of containers(); track c) {
            <label class="ar-radio" [class.on]="selectedContainer() === c" (click)="selectedContainer.set(c)">
              <span class="ar-dot"></span><span class="ar-mono">{{ c }}</span>
            </label>
          } @empty {
            <div style="color:#a1a1aa;font-size:13px">No containers found in this account.</div>
          }
        </div>
        <label class="ar-field"><span>Friendly alias</span><input class="ar-input" [(ngModel)]="alias" /></label>
        <label class="ar-field"><span>Passphrase</span><input class="ar-input ar-mono" type="password" [(ngModel)]="passphrase" /></label>
        <label class="ar-field"><span>Local path</span><input class="ar-input ar-mono" [(ngModel)]="localPath" /><small>Folder the Files view overlays against the archive.</small></label>
        @if (error()) { <div style="color:#dc2626;font-size:12.5px">{{ error() }}</div> }
        <div class="flex items-center justify-end gap-2.5" style="margin-top:22px">
          <button class="ar-btn-outline" (click)="step.set(1)">Back</button>
          <button class="ar-btn-primary" [disabled]="!selectedContainer() || !alias" (click)="add()"><i class="ki-filled ki-check"></i>Add repository</button>
        </div>
      }
    </div>
  `,
  styles: [`
    .ar-seg { display:flex;gap:8px }
    .ar-seg > button { flex:1;height:40px;border-radius:9px;border:1.5px solid #e4e4e7;background:#fff;color:#52525b;font-size:13px;font-weight:600 }
    .ar-seg > button.on { border-color:#3b82f6;background:#eff4ff;color:#3b82f6 }
    .ar-radio { display:flex;align-items:center;gap:10px;width:100%;border:1.5px solid #e4e4e7;border-radius:10px;padding:12px 14px;margin-bottom:10px;cursor:pointer;font-size:13px }
    .ar-radio.on { border-color:#3b82f6;background:#eff4ff }
    .ar-dot { width:16px;height:16px;border-radius:999px;border:2px solid #d4d4d8;flex-shrink:0 }
    .ar-radio.on .ar-dot { border-color:#3b82f6;background:#3b82f6;box-shadow:inset 0 0 0 3px #fff }
    .ar-field { display:block;margin-bottom:16px }
    .ar-field > span { display:block;font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:6px }
    .ar-field > small { display:block;font-size:11.5px;color:#a1a1aa;margin-top:5px }
    .ar-input { width:100%;height:40px;border:1px solid #e4e4e7;border-radius:9px;padding:0 12px;font-size:13.5px;outline:none }
    .ar-input:focus { border-color:#3b82f6 }
    .ar-note { font-size:12px;color:#71717a;background:#f7f9ff;border:1px solid #dbeafe;border-radius:9px;padding:10px 12px }
  `],
})
export class AddRepoWizardComponent {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);

  protected readonly accounts = toSignal(this.api.listAccounts(), { initialValue: [] });
  protected readonly step = signal<1 | 2>(1);
  protected readonly mode = signal<'select' | 'new'>('select');
  protected readonly selectedAccountId = signal(0);
  protected newName = '';
  protected newKey = '';
  protected readonly containers = signal<string[]>([]);
  protected readonly selectedContainer = signal('');
  protected alias = '';
  protected passphrase = '';
  protected localPath = '';
  protected readonly discovering = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async discover(): Promise<void> {
    this.error.set(null);
    this.discovering.set(true);
    try {
      let accountId = this.selectedAccountId();
      if (this.mode() === 'new') {
        const created = await firstValueFrom(this.api.createAccount(this.newName, this.newKey));
        accountId = created.id;
        this.selectedAccountId.set(accountId);
      }
      if (!accountId) { this.error.set('Select or create an account.'); return; }
      const names = await firstValueFrom(this.realtime.streamContainers(accountId, null, null).pipe(toArray()));
      this.containers.set(names);
      this.step.set(2);
    } catch (e: unknown) {
      this.error.set(e instanceof Error ? e.message : String(e));
    } finally {
      this.discovering.set(false);
    }
  }

  protected add(): void {
    this.error.set(null);
    this.api.createRepository({
      accountId: this.selectedAccountId(),
      container: this.selectedContainer(),
      alias: this.alias,
      passphrase: this.passphrase || null,
      localPath: this.localPath || null,
      defaultTier: null,
    }).subscribe({
      next: r => this.router.navigate(['/repos', r.id, 'files']),
      error: e => this.error.set(e?.error ?? e?.message ?? 'Could not add repository.'),
    });
  }

  protected cancel(): void { this.router.navigateByUrl('/overview'); }
}
