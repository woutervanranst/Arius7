import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { TitleCasePipe } from '@angular/common';
import { ApiService } from '../../core/api/api.service';
import { DrawerStore } from '../../core/state/drawer.store';
import { LiveConsoleComponent } from '../../shared/live-console/live-console.component';
import { formatBytes } from '../../shared/format';

/** Right slide-over for Archive and Restore, with the live stream and the restore cost-approval modal. */
@Component({
  selector: 'arius-drawer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LiveConsoleComponent, TitleCasePipe],
  template: `
    @if (arType(); as type) {
      <div class="ar-scrim" (click)="onScrim()"></div>
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
          @if (store.streamState() === 'idle') {
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
          } @else {
            <!-- Stream view -->
            <div class="flex items-center justify-between" style="margin-bottom:6px">
              <span style="font-size:13px;font-weight:600;color:#27272a">{{ stateLabel() }}</span>
              <span style="font-size:12.5px;color:#71717a">{{ store.progress() }}%</span>
            </div>
            <div data-testid="progress-bar" style="height:6px;background:#eef0f3;border-radius:999px;overflow:hidden">
              <div style="height:100%;background:#3b82f6;transition:width .3s" [style.width.%]="store.progress()"></div>
            </div>

            @if (store.stats(); as stats) {
              <div class="ar-statgrid">
                @for (kv of statEntries(); track kv[0]) {
                  <div><div class="ar-statk">{{ kv[0] }}</div><div class="ar-statv">{{ kv[1] }}</div></div>
                }
              </div>
            }

            <div style="margin-top:14px">
              <arius-live-console [lines]="store.lines()" [height]="320" />
            </div>

            @if (store.streamState() === 'done') {
              <div style="margin-top:12px;font-size:13px;color:#15803d"><i class="ki-filled ki-check-circle"></i> {{ store.summary() }}</div>
            }
          }
        </div>

        <!-- Footer -->
        <div class="flex items-center justify-end gap-2.5" style="padding:16px 20px;border-top:1px solid #f0f0f2">
          @if (store.streamState() === 'idle') {
            <button class="ar-btn-outline" (click)="store.close()">Close</button>
            <button class="ar-btn-primary" data-testid="drawer-start" (click)="store.start()">
              <i class="ki-filled {{ type === 'archive' ? 'ki-cloud-add' : 'ki-cloud-download' }}"></i>
              {{ type === 'archive' ? 'Start archive' : 'Start restore' }}
            </button>
          } @else {
            <button class="ar-btn-outline" (click)="store.close()">{{ store.streamState() === 'done' ? 'Close' : 'Hide' }}</button>
          }
        </div>

        <!-- Cost approval modal -->
        @if (store.streamState() === 'cost' && store.cost(); as cost) {
          <div class="ar-cost-scrim" data-testid="cost-modal">
            <div class="ar-cost">
              @if (cost.chunksNeedingRehydration > 0) {
                <!-- Archive restore: rehydration is required, with a priority (cost vs speed) choice. -->
                <div class="ar-cost-banner"><i class="ki-filled ki-information-2"></i> Rehydration required before download</div>
                <div class="ar-statgrid" style="margin:14px 0">
                  <div><div class="ar-statk">Ready now</div><div class="ar-statv">{{ cost.chunksAvailable }}</div></div>
                  <div><div class="ar-statk">Rehydrate</div><div class="ar-statv">{{ cost.chunksNeedingRehydration }}</div></div>
                  <div><div class="ar-statk">From archive</div><div class="ar-statv">{{ formatBytes(cost.bytesNeedingRehydration) }}</div></div>
                </div>
                <div style="font-size:13px;font-weight:600;color:#3f3f46;margin-bottom:8px">Rehydration priority</div>
                <div class="flex gap-3">
                  <button class="ar-prio" data-testid="prio-standard" [class.on]="priority() === 'standard'" (click)="priority.set('standard')">
                    <div style="font-weight:700">Standard</div><div style="font-size:12px;color:#71717a">~15 h · €{{ cost.totalStandard.toFixed(2) }}</div>
                  </button>
                  <button class="ar-prio" data-testid="prio-high" [class.on]="priority() === 'high'" (click)="priority.set('high')">
                    <div style="font-weight:700">High</div><div style="font-size:12px;color:#71717a">~1 h · €{{ cost.totalHigh.toFixed(2) }}</div>
                  </button>
                </div>
              } @else {
                <!-- Online (Hot/Cool/Cold) restore: no rehydration, just the estimated cost to approve. -->
                <div class="ar-cost-banner"><i class="ki-filled ki-information-2"></i> Estimated restore cost</div>
                <div class="ar-statgrid" style="margin:14px 0">
                  <div><div class="ar-statk">Chunks</div><div class="ar-statv">{{ cost.chunksAvailable }}</div></div>
                  <div><div class="ar-statk">Download</div><div class="ar-statv">{{ formatBytes(cost.downloadBytes) }}</div></div>
                  <div><div class="ar-statk">Est. cost</div><div class="ar-statv" data-testid="cost-total">€{{ cost.totalStandard.toFixed(2) }}</div></div>
                </div>
              }
              <div style="font-size:12px;color:#71717a;margin-top:8px">Includes data retrieval, operations, and internet egress (first 100 GB/month free).</div>
              <div class="flex items-center justify-end gap-2.5" style="margin-top:18px">
                <button class="ar-btn-outline" data-testid="cost-decline" (click)="decline()">Decline</button>
                <button class="ar-btn-primary" data-testid="cost-approve" (click)="approve()"><i class="ki-filled ki-check"></i>Approve &amp; restore</button>
              </div>
            </div>
          </div>
        }
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
    .ar-statgrid { display:grid;grid-template-columns:repeat(4,1fr);gap:10px;margin-top:14px }
    .ar-statgrid > div { background:#fafafb;border:1px solid #f0f0f2;border-radius:9px;padding:9px 11px }
    .ar-statk { font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em }
    .ar-statv { font-size:15px;font-weight:700;color:#27272a;margin-top:2px }
    .ar-cost-scrim { position:absolute;inset:0;background:rgba(255,255,255,.7);display:flex;align-items:center;justify-content:center;padding:20px }
    .ar-cost { width:100%;background:#fff;border:1px solid #e4e4e7;border-radius:14px;box-shadow:0 20px 50px rgba(9,9,11,.22);padding:20px }
    .ar-cost-banner { background:#fffbeb;border:1px solid #fde68a;color:#b45309;border-radius:9px;padding:10px 12px;font-size:13px;font-weight:600 }
    .ar-prio { flex:1;text-align:left;border:1.5px solid #e4e4e7;border-radius:11px;padding:12px 14px;background:#fff }
    .ar-prio.on { border-color:#3b82f6;background:#eff4ff }
  `],
})
export class ArchiveRestoreDrawerComponent {
  protected readonly store = inject(DrawerStore);
  private readonly api = inject(ApiService);
  protected readonly formatBytes = formatBytes;
  protected readonly tiers = ['hot', 'cool', 'cold', 'archive'];

  // This drawer only handles archive/restore; the Properties panel is a separate component.
  protected readonly arType = computed(() => {
    const t = this.store.type();
    return t === 'archive' || t === 'restore' ? t : null;
  });

  protected readonly alias = signal('repository');
  protected readonly repoLocal = signal('');
  protected readonly priority = signal<'standard' | 'high'>('standard');

  constructor() {
    effect(() => {
      const id = this.store.repoId();
      if (this.store.type() && id) {
        this.api.getRepository(id).subscribe(r => { this.alias.set(r.alias); this.repoLocal.set(r.localPath ?? ''); });
      }
    });
  }

  protected stateLabel(): string {
    switch (this.store.streamState()) {
      case 'running': return this.store.type() === 'archive' ? 'Archiving…' : 'Restoring…';
      case 'cost': return 'Awaiting cost approval';
      case 'done': return 'Done';
      default: return '';
    }
  }

  protected statEntries(): [string, string][] {
    return Object.entries(this.store.stats() ?? {});
  }

  protected onScrim(): void {
    if (this.store.streamState() === 'idle' || this.store.streamState() === 'done') this.store.close();
  }

  protected approve(): void { void this.store.approve(this.priority()); }
  protected decline(): void { void this.store.approve(null); }
}
