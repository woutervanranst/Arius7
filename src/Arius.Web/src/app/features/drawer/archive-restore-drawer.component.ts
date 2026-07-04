import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { TitleCasePipe } from '@angular/common';
import { ApiService } from '../../core/api/api.service';
import { DrawerStore } from '../../core/state/drawer.store';

/** Right slide-over for Archive and Restore: a plain idle form. Start hands the job to the floating
 *  pill and dismisses the drawer — live progress, the cost modal and warnings live on `/jobs/:id`. */
@Component({
  selector: 'arius-drawer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TitleCasePipe],
  template: `
    @if (arType(); as type) {
      <div class="ar-scrim" (click)="store.close()"></div>
      <aside class="ar-drawer" data-testid="drawer">
        <!-- Header -->
        <div class="flex items-center gap-3" style="padding:18px 20px;border-bottom:1px solid #f0f0f2">
          <div style="width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center"
               [style.background]="type === 'archive' ? '#eff6ff' : '#f5f3ff'" [style.color]="type === 'archive' ? '#3b82f6' : '#6d28d9'">
            <i class="ki-filled {{ type === 'archive' ? 'ki-cloud-add' : 'ki-cloud-download' }}" style="font-size:19px"></i>
          </div>
          <div data-testid="drawer-title" style="font-size:15.5px;font-weight:600;color:#18181b">{{ type === 'archive' ? 'Archive' : 'Restore' }} · {{ alias() }}</div>
          <button class="ms-auto ar-icon-btn" (click)="store.close()"><i class="ki-filled ki-cross"></i></button>
        </div>

        <div style="flex:1;overflow-y:auto;padding:20px">
          @if (store.error(); as error) {
            <div data-testid="start-error" style="margin-bottom:16px;font-size:12.5px;color:#b91c1c;background:#fef2f2;border:1px solid #fecaca;border-radius:9px;padding:10px 12px">
              <i class="ki-filled ki-information-2"></i> {{ error }}
            </div>
          }
          @if (type === 'archive') {
            <!-- Archive form -->
            <div class="ar-fld"><span>Source folder</span><div class="ar-ro ar-mono">{{ repoLocal() || '— set a local folder in Properties —' }}</div></div>
            <div class="ar-fld"><span>Upload tier</span>
              <div class="ar-seg">
                @for (t of tiers; track t) {
                  <button data-testid="tier-seg" [attr.data-tier]="t" [class.on]="store.archiveTier() === t" (click)="store.archiveTier.set(t)">{{ t | titlecase }}</button>
                }
              </div>
            </div>
            <div class="ar-fld">
              <span>On disk after archive:</span>
              <div class="ar-seg">
                <button data-testid="seg-on-disk" [attr.data-on-disk]="'keep'" [class.on]="store.archiveOnDisk() === 'keep'" (click)="store.archiveOnDisk.set('keep')">Keep files only</button>
                <button data-testid="seg-on-disk" [attr.data-on-disk]="'keep-pointers'" [class.on]="store.archiveOnDisk() === 'keep-pointers'" (click)="store.archiveOnDisk.set('keep-pointers')">Keep files + pointers</button>
                <button data-testid="seg-on-disk" [attr.data-on-disk]="'replace'" [class.on]="store.archiveOnDisk() === 'replace'" (click)="store.archiveOnDisk.set('replace')">Replace with pointers</button>
              </div>
            </div>
            <label class="ar-toggle"><input type="checkbox" data-testid="toggle-fast-hash" [checked]="store.fastHash()" (change)="store.fastHash.set(!store.fastHash())" /> Fast hash — skip re-reading unchanged files</label>
          } @else {
            <!-- Restore form -->
            <div class="ar-target">
              <i class="ki-filled ki-cloud-download" style="color:#6d28d9"></i>
              <div>
                <div style="font-weight:600;color:#27272a">{{ store.collectedPaths().length === 0 ? 'Whole repository' : store.collectedPaths().length + ' collected files' }}</div>
                <div style="font-size:12px;color:#a1a1aa">{{ store.version() ? 'Snapshot ' + store.version() : 'Latest snapshot' }}</div>
              </div>
            </div>
            <label class="ar-toggle"><input type="checkbox" [checked]="store.overwrite()" (change)="store.overwrite.set(!store.overwrite())" /> Overwrite existing files</label>
            <label class="ar-toggle"><input type="checkbox" [checked]="store.restoreNoPointers()" (change)="store.restoreNoPointers.set(!store.restoreNoPointers())" /> Skip pointer files</label>
            <div class="ar-note">Archive-tier chunks require rehydration; you'll be asked to approve the cost before any download begins.</div>
          }
        </div>

        <!-- Footer -->
        <div class="flex items-center justify-end gap-2.5" style="padding:16px 20px;border-top:1px solid #f0f0f2">
          <button class="ar-btn-outline" (click)="store.close()">Close</button>
          <button class="ar-btn-primary" data-testid="drawer-start" (click)="store.start()">
            <i class="ki-filled {{ type === 'archive' ? 'ki-cloud-add' : 'ki-cloud-download' }}"></i>
            {{ type === 'archive' ? 'Start archive' : 'Start restore' }}
          </button>
        </div>
      </aside>
    }
  `,
  styles: [`
    .ar-scrim { position:fixed;inset:0;z-index:40;background:rgba(9,9,11,.18);animation:ar-fade .2s }
    .ar-drawer { position:fixed;top:0;right:0;bottom:0;z-index:41;width:494px;max-width:92%;background:#fff;display:flex;flex-direction:column;box-shadow:-12px 0 40px rgba(9,9,11,.18);animation:ar-slide .26s cubic-bezier(.4,0,.2,1) }
    @keyframes ar-fade { from { opacity:0 } to { opacity:1 } }
    @keyframes ar-slide { from { transform:translateX(100%) } to { transform:translateX(0) } }
    .ar-icon-btn { width:30px;height:30px;border-radius:8px;border:1px solid #e4e4e7;color:#71717a;display:flex;align-items:center;justify-content:center }
    .ar-fld { margin-bottom:16px }
    .ar-fld > span { display:block;font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:7px }
    .ar-ro { background:#f7f7f8;border:1px solid #ececef;border-radius:9px;padding:9px 12px;font-size:12.5px;color:#52525b }
    .ar-seg { display:flex;gap:8px }
    .ar-seg > button { flex:1;height:40px;border-radius:9px;border:1.5px solid #e4e4e7;background:#fff;color:#52525b;font-size:13px;font-weight:600 }
    .ar-seg > button.on { border-color:#3b82f6;background:#eff4ff;color:#3b82f6 }
    .ar-toggle { display:flex;align-items:center;gap:9px;font-size:13.5px;color:#3f3f46;margin-bottom:12px }
    .ar-toggle small { color:#a1a1aa }
    .ar-note { font-size:12px;color:#71717a;background:#f7f9ff;border:1px solid #dbeafe;border-radius:9px;padding:10px 12px;margin-top:6px }
    .ar-target { display:flex;align-items:center;gap:12px;background:#faf5ff;border:1px solid #e9d5ff;border-radius:11px;padding:14px 16px;margin-bottom:16px }
  `],
})
export class ArchiveRestoreDrawerComponent {
  protected readonly store = inject(DrawerStore);
  private readonly api = inject(ApiService);
  protected readonly tiers = ['hot', 'cool', 'cold', 'archive'];

  // This drawer only handles archive/restore; the Properties panel is a separate component.
  protected readonly arType = computed(() => {
    const t = this.store.type();
    return t === 'archive' || t === 'restore' ? t : null;
  });

  protected readonly alias = signal('repository');
  protected readonly repoLocal = signal('');

  constructor() {
    effect(() => {
      const id = this.store.repoId();
      if (this.store.type() && id) {
        this.api.getRepository(id).subscribe(r => { this.alias.set(r.alias); this.repoLocal.set(r.localPath ?? ''); });
      }
    });
  }
}
