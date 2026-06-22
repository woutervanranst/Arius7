import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { SnapshotStore } from '../../core/state/snapshot.store';

/**
 * Snapshot time-travel bar: picker dropdown + scrubber, rendered in the repo shell above the tabs
 * so the selection applies across Files (and any future snapshot-scoped tab). Backed by SnapshotStore.
 */
@Component({
  selector: 'arius-snapshot-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <div class="ar-card" style="padding:13px 18px;display:flex;align-items:center;gap:18px">
      <div style="position:relative">
        <button class="ar-btn-outline" data-testid="snapshot-picker" (click)="pickerOpen.set(!pickerOpen())">
          <i class="ki-filled ki-time"></i>
          <span>Snapshot <b>{{ activeSnapLabel() }}</b></span>
          @if (!snap.version()) { <span class="ar-pill-green">LATEST</span> }
          <i class="ki-filled ki-down" style="font-size:13px"></i>
        </button>
        @if (pickerOpen()) {
          <div class="ar-snap-menu">
            @for (item of menuItems(); track item.version) {
              <button class="ar-snap-item" data-testid="snapshot-item" (click)="pick(item.index)">
                <span style="font-weight:600">v{{ item.version }}</span>
                <span style="color:#71717a">{{ item.timestamp | date:'dd MMM yyyy · HH:mm' }}</span>
                @if (item.isLatest) { <span class="ar-pill-green">LATEST</span> }
              </button>
            } @empty {
              <div style="padding:12px;color:#a1a1aa;font-size:12.5px">No snapshots</div>
            }
          </div>
        }
      </div>

      <!-- Scrubber -->
      <div style="flex:1;display:flex;align-items:center;gap:10px;height:20px">
        <div style="position:relative;flex:1;height:4px;background:#eef0f3;border-radius:999px">
          @for (s of snap.snapshots(); track s.version; let i = $index) {
            <span class="ar-scrub-dot" data-testid="scrubber-dot" [class.active]="i === snap.activeIndex()" [class.past]="i < snap.activeIndex()"
                  [style.left.%]="dotLeft(i)" (click)="pick(i)"></span>
          }
        </div>
      </div>

      @if (snap.version()) {
        <span class="ar-pill-amber">Historical view</span>
      } @else {
        <span style="font-size:12.5px;color:#16a34a;font-weight:600">● Live working state</span>
      }
    </div>
  `,
  styles: [`
    .ar-pill-green { font-size:10px;font-weight:700;color:#15803d;background:#f0fdf4;border-radius:999px;padding:1px 7px;letter-spacing:.04em }
    .ar-pill-amber { font-size:11.5px;font-weight:600;color:#b45309;background:#fffbeb;border:1px solid #fde68a;border-radius:999px;padding:3px 10px }
    .ar-snap-menu { position:absolute;top:46px;left:0;z-index:30;width:max-content;min-width:240px;max-width:440px;background:#fff;border:1px solid #e4e4e7;border-radius:11px;box-shadow:0 12px 32px rgba(9,9,11,.14);padding:6px;max-height:320px;overflow-y:auto }
    .ar-snap-item { display:flex;align-items:center;gap:10px;width:100%;padding:8px 10px;border-radius:8px;font-size:12.5px;text-align:left;white-space:nowrap }
    .ar-snap-item:hover { background:#f7f9ff }
    .ar-scrub-dot { position:absolute;top:50%;width:11px;height:11px;border-radius:999px;background:#d8dce2;transform:translate(-50%,-50%);cursor:pointer;border:2px solid #fff }
    .ar-scrub-dot.past { background:#bcd3f5 }
    .ar-scrub-dot.active { width:15px;height:15px;background:#3b82f6 }
  `],
})
export class SnapshotBarComponent {
  protected readonly snap = inject(SnapshotStore);
  readonly repoId = input.required<number>();
  protected readonly pickerOpen = signal(false);

  constructor() {
    effect(() => this.snap.load(this.repoId()));
  }

  protected readonly activeSnapLabel = computed(() => {
    const list = this.snap.snapshots();
    if (!list.length) return '—';
    return 'v' + (this.snap.activeIndex() + 1);
  });

  // Dropdown rows newest-first (most recent at the top), carrying the original oldest-first store index
  // so pick()/select() still map the last index to "latest". The version number stays 1-based oldest-first.
  protected readonly menuItems = computed(() => {
    const list = this.snap.snapshots();
    return list
      .map((s, i) => ({ version: i + 1, index: i, timestamp: s.timestamp, isLatest: i === list.length - 1 }))
      .reverse();
  });

  protected dotLeft(i: number): number {
    const n = this.snap.snapshots().length;
    return n <= 1 ? 0 : (i / (n - 1)) * 100;
  }

  protected pick(index: number): void {
    this.pickerOpen.set(false);
    this.snap.select(index);
  }
}
