import { ChangeDetectionStrategy, Component, DestroyRef, afterNextRender, afterRenderEffect, computed, effect, inject, input, signal, viewChild, ElementRef } from '@angular/core';
import { formatDate } from '@angular/common';
import { SnapshotStore } from '../../core/state/snapshot.store';
import { layoutSnapshotDots } from './snapshot-layout';

const DATE_FORMAT = 'dd MMM yyyy · HH:mm';

/**
 * Snapshot time-travel bar: picker dropdown + scrubber, rendered in the repo shell above the tabs
 * so the selection applies across Files (and any future snapshot-scoped tab). Backed by SnapshotStore.
 */
@Component({
  selector: 'arius-snapshot-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ar-card" style="padding:13px 18px;display:flex;align-items:center;gap:18px">
      <div style="position:relative;flex:none">
        <button class="ar-btn-outline ar-snap-picker" data-testid="snapshot-picker" aria-haspopup="listbox" [attr.aria-expanded]="pickerOpen()" (click)="pickerOpen.set(!pickerOpen())">
          <i class="ki-filled ki-time" style="flex:none;font-size:14px"></i>
          <span class="ar-snap-label">
            @if (activeEntry(); as a) {
              <b>v{{ a.version }}</b><span class="ar-snap-date">{{ a.label }}</span>
            } @else { — }
          </span>
          @if (!snap.version() && snap.snapshots().length) { <span class="ar-pill-green">LATEST</span> }
          <i class="ki-filled ki-down" style="font-size:11px;flex:none"></i>
        </button>
        @if (pickerOpen()) {
          <div class="ar-snap-menu" role="listbox">
            @for (item of menuItems(); track item.version) {
              <button class="ar-snap-item" [class.sel]="item.index === snap.activeIndex()" data-testid="snapshot-item" (click)="pick(item.index)">
                <span style="font-weight:600">v{{ item.version }}</span>
                <span style="color:#71717a">{{ item.label }}</span>
                <span style="justify-self:end">@if (item.isLatest) { <span class="ar-pill-green">LATEST</span> }</span>
              </button>
            } @empty {
              <div style="padding:8px;color:#a1a1aa;font-size:12.5px">No snapshots</div>
            }
          </div>
        }
      </div>

      <!-- Scrubber: dots at their real point in time, hover/focus shows the datetime -->
      <div #timeline class="ar-snap-timeline">
        <div class="ar-snap-track"></div>
        <div class="ar-snap-fill" [style.width.px]="activeX()"></div>
        @for (e of entries(); track e.version; let i = $index) {
          <button class="ar-scrub-dot" data-testid="scrubber-dot" [class.active]="i === snap.activeIndex()" [class.past]="i < snap.activeIndex()"
                  [style.left.px]="dotX()[i]" [attr.aria-label]="'v' + e.version + ', ' + e.label"
                  (click)="pick(i)" (mouseenter)="showTip(i)" (mouseleave)="tipVisible.set(false)"
                  (focus)="showTip(i)" (blur)="tipVisible.set(false)"></button>
        }
        <div #tip class="ar-snap-tip" [class.show]="tipVisible()" [style.left.px]="tipLeft()" [style.--arrow.px]="tipArrow()" role="tooltip">
          @if (tipEntry(); as t) { <b>v{{ t.version }}</b><span>{{ t.label }}</span> }
        </div>
      </div>

      <div class="ar-snap-status">
        @if (snap.version()) {
          <span class="ar-pill-amber">Historical view</span>
        } @else {
          <span style="font-size:12.5px;color:#16a34a;font-weight:600">● Live working state</span>
        }
      </div>
    </div>
  `,
  styles: [`
    .ar-pill-green { font-size:10px;font-weight:700;color:#15803d;background:#f0fdf4;border-radius:999px;padding:1px 7px;letter-spacing:.04em }
    .ar-pill-amber { font-size:11.5px;font-weight:600;color:#b45309;background:#fffbeb;border:1px solid #fde68a;border-radius:999px;padding:3px 10px;white-space:nowrap }
    .ar-snap-picker { width:272px;height:36px;justify-content:space-between;white-space:nowrap;gap:6px;padding:0 12px }
    .ar-snap-label { flex:1;text-align:left;overflow:hidden }
    .ar-snap-date { color:#71717a;font-weight:400;margin-left:4px }
    .ar-snap-menu { position:absolute;top:42px;left:0;z-index:30;width:236px;background:#fff;border:1px solid #e4e4e7;border-radius:10px;box-shadow:0 10px 28px rgba(9,9,11,.14);padding:4px;max-height:300px;overflow-y:auto }
    .ar-snap-item { display:grid;grid-template-columns:26px auto 1fr;align-items:center;gap:10px;width:100%;padding:4px 8px;border-radius:6px;font-size:12.5px;line-height:1.3;text-align:left;white-space:nowrap }
    .ar-snap-item:hover { background:#f7f9ff }
    .ar-snap-item.sel { background:#eff6ff }
    .ar-snap-timeline { position:relative;flex:1;height:36px;min-width:200px }
    .ar-snap-track { position:absolute;left:0;right:0;top:50%;height:4px;margin-top:-2px;background:#eef0f3;border-radius:999px }
    .ar-snap-fill { position:absolute;left:0;top:50%;height:4px;margin-top:-2px;background:#bcd3f5;border-radius:999px }
    .ar-scrub-dot { position:absolute;top:50%;width:11px;height:11px;border-radius:999px;background:#d8dce2;transform:translate(-50%,-50%);cursor:pointer;border:2px solid #fff;padding:0;transition:width .12s,height .12s,background .12s }
    .ar-scrub-dot.past { background:#bcd3f5 }
    .ar-scrub-dot:hover { background:#93b9f0;width:15px;height:15px }
    .ar-scrub-dot.active { width:15px;height:15px;background:#3b82f6 }
    .ar-scrub-dot:focus-visible { outline:2px solid #3b82f6;outline-offset:2px }
    .ar-snap-tip { position:absolute;bottom:calc(50% + 14px);transform:translateX(-50%);background:#18181b;color:#fff;border-radius:8px;padding:6px 10px;white-space:nowrap;font-size:12px;pointer-events:none;opacity:0;transition:opacity .1s;z-index:20 }
    .ar-snap-tip.show { opacity:1 }
    .ar-snap-tip b { margin-right:6px }
    .ar-snap-tip span { color:#d4d4d8 }
    .ar-snap-tip::after { content:"";position:absolute;left:calc(50% + var(--arrow,0px));top:100%;margin-left:-5px;border:5px solid transparent;border-top-color:#18181b }
    .ar-snap-status { width:150px;flex:none;text-align:right }
  `],
})
export class SnapshotBarComponent {
  protected readonly snap = inject(SnapshotStore);
  readonly repoId = input.required<number>();
  protected readonly pickerOpen = signal(false);

  private readonly timeline = viewChild.required<ElementRef<HTMLElement>>('timeline');
  private readonly tip = viewChild.required<ElementRef<HTMLElement>>('tip');
  private readonly trackWidth = signal(0);
  // The tooltip keeps its last dot while fading out, so it never empties mid-transition.
  protected readonly tipIndex = signal<number | null>(null);
  protected readonly tipVisible = signal(false);
  protected readonly tipLeft = signal(0);
  protected readonly tipArrow = signal(0);

  constructor() {
    effect(() => this.snap.load(this.repoId()));

    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const el = this.timeline().nativeElement;
      this.trackWidth.set(el.clientWidth);
      const ro = new ResizeObserver(() => this.trackWidth.set(el.clientWidth));
      ro.observe(el);
      destroyRef.onDestroy(() => ro.disconnect());
    });

    // Place the tooltip once it has rendered (its width is only known then): centred on the dot, clamped
    // to the track edges, with the arrow offset so it still points at the dot.
    afterRenderEffect(() => {
      const i = this.tipIndex();
      const x = i === null ? undefined : this.dotX()[i];
      if (x === undefined) return;
      const w = this.trackWidth();
      const tw = this.tip().nativeElement.offsetWidth;
      const left = Math.max(tw / 2, Math.min(x, w - tw / 2));
      this.tipLeft.set(left);
      this.tipArrow.set(x - left);
    });
  }

  // Oldest-first like the store; version numbers are 1-based and oldest-first.
  protected readonly entries = computed(() => {
    const list = this.snap.snapshots();
    return list.map((s, i) => ({
      version: i + 1,
      index: i,
      time: new Date(s.timestamp).getTime(),
      label: formatDate(s.timestamp, DATE_FORMAT, 'en-US'),
      isLatest: i === list.length - 1,
    }));
  });

  protected readonly activeEntry = computed(() => this.entries()[this.snap.activeIndex()]);
  protected readonly tipEntry = computed(() => {
    const i = this.tipIndex();
    return i === null ? undefined : this.entries()[i];
  });

  protected readonly dotX = computed(() => layoutSnapshotDots(this.entries().map(e => e.time), this.trackWidth()));
  protected readonly activeX = computed(() => this.dotX()[this.snap.activeIndex()] ?? 0);

  // Dropdown rows newest-first (most recent at the top), carrying the original oldest-first store index
  // so pick()/select() still map the last index to "latest".
  protected readonly menuItems = computed(() => [...this.entries()].reverse());

  protected showTip(index: number): void {
    this.tipIndex.set(index);
    this.tipVisible.set(true);
  }

  protected pick(index: number): void {
    this.pickerOpen.set(false);
    this.snap.select(index);
  }
}
