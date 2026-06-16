import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { StateRingComponent } from '../state-ring/state-ring.component';

/** Footer "State legend" button + an upward popover explaining the ring anatomy and colour key. */
@Component({
  selector: 'arius-state-legend',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StateRingComponent],
  template: `
    <div style="position:relative">
      <button class="flex items-center gap-1.5" style="font-size:12.5px;color:#71717a" (click)="open.set(!open())">
        <arius-state-ring [size]="15" [colors]="{ lo:'#27272a', li:'#2563eb', ro:'#27272a', ri:'#2563eb' }" />
        State legend
      </button>
      @if (open()) {
        <div style="position:absolute;bottom:28px;right:0;z-index:30;width:300px;background:#fff;border:1px solid #e4e4e7;border-radius:12px;box-shadow:0 12px 32px rgba(9,9,11,.18);padding:16px">
          <div class="flex items-start gap-4">
            <arius-state-ring [size]="76" [colors]="{ lo:'#27272a', li:'#2563eb', ro:'#27272a', ri:'#9cc4f5' }" />
            <div style="font-size:12px;color:#52525b;line-height:1.5">
              <div><b>Left</b> = local disk · <b>right</b> = repository.</div>
              <div>Outer ring = pointer / filetree entry; inner disc = binary / chunk.</div>
            </div>
          </div>
          <div style="margin-top:12px;display:flex;flex-direction:column;gap:6px;font-size:12px;color:#52525b">
            <div class="flex items-center gap-2"><span class="ar-key" style="background:#27272a"></span>Present (pointer / filetree)</div>
            <div class="flex items-center gap-2"><span class="ar-key" style="background:#2563eb"></span>Binary on disk / chunk hydrated</div>
            <div class="flex items-center gap-2"><span class="ar-key" style="background:#9cc4f5"></span>Chunk not hydrated (archive tier)</div>
            <div class="flex items-center gap-2"><span class="ar-key" style="background:#e4e7ec"></span>Absent</div>
          </div>
        </div>
      }
    </div>
  `,
  styles: [`.ar-key { width:12px;height:12px;border-radius:3px;display:inline-block }`],
})
export class StateLegendComponent {
  protected readonly open = signal(false);
}
