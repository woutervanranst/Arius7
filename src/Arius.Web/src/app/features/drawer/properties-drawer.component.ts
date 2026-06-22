import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { DrawerStore } from '../../core/state/drawer.store';
import { PropertiesTabComponent } from '../repo/properties/properties-tab.component';

/** Right slide-over hosting the repository Properties form (alias, account/container, key, schedules). */
@Component({
  selector: 'arius-properties-drawer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PropertiesTabComponent],
  template: `
    @if (open()) {
      <div class="ar-scrim" (click)="store.close()"></div>
      <aside class="ar-drawer" data-testid="properties-drawer">
        <!-- Header -->
        <div class="flex items-center gap-3" style="padding:18px 20px;border-bottom:1px solid #f0f0f2">
          <div style="width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center;background:#f4f4f5;color:#52525b">
            <i class="ki-filled ki-setting-2" style="font-size:19px"></i>
          </div>
          <div style="font-size:15.5px;font-weight:600;color:#18181b">Properties</div>
          <button class="ms-auto ar-icon-btn" (click)="store.close()"><i class="ki-filled ki-cross"></i></button>
        </div>

        <div style="flex:1;overflow-y:auto;padding:20px">
          <arius-properties-tab [repoId]="repoId()" />
        </div>
      </aside>
    }
  `,
  styles: [`
    .ar-scrim { position:fixed;inset:0;z-index:40;background:rgba(9,9,11,.18);animation:ar-fade .2s }
    .ar-drawer { position:fixed;top:0;right:0;bottom:0;z-index:41;width:560px;max-width:92%;background:#fff;display:flex;flex-direction:column;box-shadow:-12px 0 40px rgba(9,9,11,.18);animation:ar-slide .26s cubic-bezier(.4,0,.2,1) }
    @keyframes ar-fade { from { opacity:0 } to { opacity:1 } }
    @keyframes ar-slide { from { transform:translateX(100%) } to { transform:translateX(0) } }
    .ar-icon-btn { width:30px;height:30px;border-radius:8px;border:1px solid #e4e4e7;color:#71717a;display:flex;align-items:center;justify-content:center }
  `],
})
export class PropertiesDrawerComponent {
  protected readonly store = inject(DrawerStore);
  protected readonly open = computed(() => this.store.type() === 'properties');
  protected readonly repoId = computed(() => String(this.store.repoId()));
}
