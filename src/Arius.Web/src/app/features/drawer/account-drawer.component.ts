import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { DrawerStore } from '../../core/state/drawer.store';
import { ApiService } from '../../core/api/api.service';
import { AccountDto } from '../../core/api/api-models';

/** Right slide-over to edit a storage account: rotate the account key and set its Azure region. */
@Component({
  selector: 'arius-account-drawer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    @if (open()) {
      <div class="ar-scrim" (click)="store.close()"></div>
      <aside class="ar-drawer" data-testid="account-drawer">
        <!-- Header -->
        <div class="flex items-center gap-3" style="padding:18px 20px;border-bottom:1px solid #f0f0f2">
          <div style="width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center;background:#f4f4f5;color:#52525b">
            <i class="ki-filled ki-cloud" style="font-size:19px"></i>
          </div>
          <div style="font-size:15.5px;font-weight:600;color:#18181b">Edit storage account</div>
          <button class="ms-auto ar-icon-btn" (click)="store.close()"><i class="ki-filled ki-cross"></i></button>
        </div>

        <div style="flex:1;overflow-y:auto;padding:20px">
          @if (account(); as a) {
            <label class="ar-field">
              <span>Account name</span>
              <input class="ar-input ar-mono" [value]="a.name" readonly />
            </label>

            <label class="ar-field">
              <span>Account key</span>
              <input class="ar-input ar-mono" type="password" data-testid="account-key"
                     [placeholder]="a.hasKey ? '•••••••• (stored encrypted — replace to rotate)' : 'Paste the account key'"
                     [(ngModel)]="key" />
              <small>Stored encrypted in the Arius.Api SQLite. Leave blank to keep the current key.</small>
            </label>

            <label class="ar-field">
              <span>Region</span>
              <select class="ar-input" data-testid="account-region" [ngModel]="region()" (ngModelChange)="region.set($event)">
                <option value="">Unknown / Not in list</option>
                @for (r of regions(); track r) {
                  <option [value]="r">{{ r }}</option>
                }
              </select>
              <small>Drives the storage-cost estimate. "Unknown" prices against the default region.</small>
            </label>

            <div class="flex items-center justify-end gap-2.5" style="margin-top:18px">
              <button class="ar-btn-outline" (click)="store.close()">Close</button>
              <button class="ar-btn-primary" data-testid="account-save" (click)="save(a)"><i class="ki-filled ki-check"></i>Save changes</button>
            </div>
            @if (saved()) { <div style="text-align:right;color:#15803d;font-size:12.5px;margin-top:8px">Saved.</div> }
            @if (saveError()) { <div data-testid="account-save-error" style="text-align:right;color:#dc2626;font-size:12.5px;margin-top:8px">{{ saveError() }}</div> }

            <!-- Danger zone -->
            <div class="ar-card" style="padding:16px 18px;margin-top:22px;border-color:#fecaca">
              <div style="font-size:14px;font-weight:600;color:#b91c1c">Delete account</div>
              @if (a.repositories > 0) {
                <p style="font-size:12.5px;color:#a1a1aa;margin:4px 0 0">In use by {{ a.repositories }} {{ a.repositories === 1 ? 'repository' : 'repositories' }} — remove those first.</p>
              } @else if (confirmingDelete()) {
                <div class="flex items-center gap-2.5" style="margin-top:10px">
                  <span style="font-size:13px;color:#b91c1c;margin-right:auto">Remove this account?</span>
                  <button class="ar-btn-outline" (click)="confirmingDelete.set(false)">Cancel</button>
                  <button class="ar-btn-danger" data-testid="account-delete-confirm" (click)="remove(a)"><i class="ki-filled ki-trash"></i>Confirm</button>
                </div>
              } @else {
                <button class="ar-btn-danger" data-testid="account-delete" style="margin-top:10px" (click)="confirmingDelete.set(true)"><i class="ki-filled ki-trash"></i>Delete account</button>
              }
              @if (deleteError()) { <div style="color:#dc2626;font-size:12.5px;margin-top:10px">{{ deleteError() }}</div> }
            </div>
          } @else {
            <div style="padding:24px;color:#a1a1aa;font-size:13px">Loading…</div>
          }
        </div>
      </aside>
    }
  `,
  styles: [`
    .ar-scrim { position:fixed;inset:0;z-index:40;background:rgba(9,9,11,.18);animation:ar-fade .2s }
    .ar-drawer { position:fixed;top:0;right:0;bottom:0;z-index:41;width:520px;max-width:92%;background:#fff;display:flex;flex-direction:column;box-shadow:-12px 0 40px rgba(9,9,11,.18);animation:ar-slide .26s cubic-bezier(.4,0,.2,1) }
    @keyframes ar-fade { from { opacity:0 } to { opacity:1 } }
    @keyframes ar-slide { from { transform:translateX(100%) } to { transform:translateX(0) } }
    .ar-icon-btn { width:30px;height:30px;border-radius:8px;border:1px solid #e4e4e7;color:#71717a;display:flex;align-items:center;justify-content:center }
    .ar-field { display:block;margin-bottom:16px }
    .ar-field > span { display:block;font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:6px }
    .ar-field > small { display:block;font-size:11.5px;color:#a1a1aa;margin-top:5px }
    .ar-input { width:100%;height:40px;border:1px solid #e4e4e7;border-radius:9px;padding:0 12px;font-size:13.5px;color:#27272a;outline:none;background:#fff }
    .ar-input:focus { border-color:#3b82f6 }
    .ar-input[readonly] { background:#f7f7f8;color:#71717a }
  `],
})
export class AccountDrawerComponent {
  protected readonly store = inject(DrawerStore);
  private readonly api = inject(ApiService);

  protected readonly open = computed(() => this.store.type() === 'account');
  protected readonly regions = toSignal(this.api.getRegions(), { initialValue: [] as string[] });

  protected readonly account = signal<AccountDto | null>(null);
  protected key = '';
  protected readonly region = signal('');
  protected readonly saved = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly deleteError = signal<string | null>(null);
  protected readonly confirmingDelete = signal(false);

  constructor() {
    // (Re)load the account whenever the drawer opens for a (possibly different) account id.
    effect(onCleanup => {
      if (this.store.type() !== 'account') return;
      const id = this.store.accountId();
      this.account.set(null);
      this.key = '';
      this.saved.set(false);
      this.saveError.set(null);
      this.deleteError.set(null);
      this.confirmingDelete.set(false);
      const sub = this.api.getAccount(id).subscribe(a => { this.account.set(a); this.region.set(a.location ?? ''); });
      onCleanup(() => sub.unsubscribe());
    });
  }

  protected save(a: AccountDto): void {
    this.saveError.set(null);
    this.api.updateAccount(a.id, { accountKey: this.key || null, location: this.region() || null }).subscribe({
      next: updated => { this.account.set(updated); this.key = ''; this.saved.set(true); this.store.bumpAccounts(); },
      error: () => this.saveError.set('Could not save changes — please try again.'),
    });
  }

  protected remove(a: AccountDto): void {
    this.deleteError.set(null);
    this.api.deleteAccount(a.id).subscribe({
      next: () => { this.store.bumpAccounts(); this.store.close(); },
      error: () => this.deleteError.set('Could not delete the account — please try again.'),
    });
  }
}
