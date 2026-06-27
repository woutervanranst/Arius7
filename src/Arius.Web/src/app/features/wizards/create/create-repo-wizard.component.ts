import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TitleCasePipe } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../../core/api/api.service';
import { FolderPickerComponent } from '../../../shared/folder-picker/folder-picker.component';

/** New repository: 2-step wizard (storage account → new container with tier + passphrase). */
@Component({
  selector: 'arius-create-wizard',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TitleCasePipe, FolderPickerComponent],
  template: `
    <div style="max-width:620px;margin:0 auto">
      <div style="font-size:13px;color:#71717a">Step <b>{{ step() }}</b> of 2 · {{ step() === 1 ? 'Storage account' : 'New container' }}</div>
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
            <label class="ar-radio" data-testid="account-radio" [class.on]="selectedAccountId() === a.id" (click)="selectedAccountId.set(a.id)">
              <span class="ar-dot"></span><span class="ar-mono" style="font-weight:600">{{ a.name }}</span>
              <span style="color:#a1a1aa;margin-left:auto">{{ a.repositories }} repositories</span>
            </label>
          } @empty { <div style="color:#a1a1aa;font-size:13px">No configured accounts — add a new one.</div> }
        } @else {
          <label class="ar-field"><span>Account name <span class="ar-req">*</span></span><input class="ar-input ar-mono" data-testid="new-account-name" [(ngModel)]="newName" /></label>
          <label class="ar-field"><span>Account key <span class="ar-req">*</span></span><input class="ar-input ar-mono" type="password" data-testid="new-account-key" [(ngModel)]="newKey" /></label>
          <label class="ar-field"><span>Region</span>
            <select class="ar-input" data-testid="new-account-region" [ngModel]="newRegion()" (ngModelChange)="newRegion.set($event)">
              <option value="">Unknown / Not in list</option>
              @for (r of regions(); track r) { <option [value]="r">{{ r }}</option> }
            </select>
          </label>
        }
        @if (error()) { <div style="color:#dc2626;font-size:12.5px;margin-top:8px">{{ error() }}</div> }
        <div class="flex items-center justify-end gap-2.5" style="margin-top:22px">
          <button class="ar-btn-outline" (click)="cancel()">Cancel</button>
          <button class="ar-btn-primary" data-testid="btn-continue" (click)="next()"><i class="ki-filled ki-arrow-right"></i>Continue</button>
        </div>
      } @else {
        <h1 class="ar-heading" style="font-size:20px;font-weight:700;margin-bottom:16px">New container</h1>
        <label class="ar-field"><span>Container name <span class="ar-req">*</span></span><input class="ar-input ar-mono" data-testid="create-container" [ngModel]="container()" (ngModelChange)="container.set($event)" placeholder="e.g. arius-photos" /></label>
        <label class="ar-field"><span>Friendly alias</span><input class="ar-input" data-testid="create-alias" [(ngModel)]="alias" [placeholder]="container() || 'defaults to the container name'" /></label>
        <div class="ar-field"><span>Local path</span><arius-folder-picker [(value)]="localPath" /></div>
        <div class="ar-field"><span>Default tier</span>
          <div class="ar-seg">
            @for (t of tiers; track t) { <button [class.on]="tier() === t" (click)="tier.set(t)">{{ t | titlecase }}</button> }
          </div>
        </div>
        <label class="ar-field"><span>Passphrase <span class="ar-req">*</span></span><input class="ar-input ar-mono" type="password" data-testid="passphrase" [(ngModel)]="passphrase" /></label>
        <label class="ar-field"><span>Confirm passphrase <span class="ar-req">*</span></span><input class="ar-input ar-mono" type="password" data-testid="passphrase-confirm" [(ngModel)]="passphrase2" /></label>
        <div style="font-size:12px;color:#b45309;background:#fffbeb;border:1px solid #fde68a;border-radius:9px;padding:10px 12px">
          <i class="ki-filled ki-information-2"></i> The passphrase encrypts every chunk and <b>cannot be recovered</b> if lost.
        </div>
        @if (error()) { <div style="color:#dc2626;font-size:12.5px;margin-top:8px">{{ error() }}</div> }
        <div class="flex items-center justify-end gap-2.5" style="margin-top:22px">
          <button class="ar-btn-outline" (click)="step.set(1)">Back</button>
          <button class="ar-btn-primary" data-testid="btn-create" [disabled]="!canCreate()" (click)="create()"><i class="ki-filled ki-check"></i>Create repository</button>
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
    .ar-input { width:100%;height:40px;border:1px solid #e4e4e7;border-radius:9px;padding:0 12px;font-size:13.5px;outline:none }
    .ar-input:focus { border-color:#3b82f6 }
    .ar-input[readonly] { background:#f7f7f8;color:#71717a }
    .ar-req { color:#dc2626 }
  `],
})
export class CreateRepoWizardComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  protected readonly accounts = toSignal(this.api.listAccounts(), { initialValue: [] });
  protected readonly regions = toSignal(this.api.getRegions(), { initialValue: [] as string[] });
  protected readonly step = signal<1 | 2>(1);
  protected readonly mode = signal<'select' | 'new'>('select');
  protected readonly selectedAccountId = signal(0);
  protected newName = '';
  protected newKey = '';
  protected readonly newRegion = signal('');
  protected alias = '';
  protected readonly container = signal('');
  protected localPath = '';
  protected readonly tier = signal('cold');
  protected passphrase = '';
  protected passphrase2 = '';
  protected readonly error = signal<string | null>(null);
  protected readonly tiers = ['hot', 'cool', 'cold', 'archive'];

  // A method (not a computed) because alias/passphrase are plain ngModel properties, not signals —
  // OnPush re-evaluates this on each ngModel change. Container is required; the friendly alias is optional.
  protected canCreate(): boolean {
    return !!this.container() && !!this.passphrase && this.passphrase === this.passphrase2;
  }

  protected async next(): Promise<void> {
    this.error.set(null);
    try {
      if (this.mode() === 'new') {
        const created = await firstValueFrom(this.api.createAccount(this.newName, this.newKey, this.newRegion() || null));
        this.selectedAccountId.set(created.id);
      }
      if (!this.selectedAccountId()) { this.error.set('Select or create an account.'); return; }
      this.step.set(2);
    } catch (e: unknown) {
      this.error.set(e instanceof Error ? e.message : String(e));
    }
  }

  protected create(): void {
    this.error.set(null);
    this.api.createRepository({
      accountId: this.selectedAccountId(),
      container: this.container(),
      // Friendly alias is optional — fall back to the container name when left blank.
      alias: this.alias || this.container(),
      passphrase: this.passphrase || null,
      localPath: this.localPath || null,
      defaultTier: this.tier(),
    }).subscribe({
      next: r => this.router.navigate(['/repos', r.id, 'files']),
      error: e => this.error.set(e?.error ?? e?.message ?? 'Could not create repository.'),
    });
  }

  protected cancel(): void { this.router.navigateByUrl('/overview'); }
}
