import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * Byte-weighted layered progress bar (design README §Screens 2). ONE track, overlapping fills painted
 * largest-first so each subset shows on top of the previous. Archive: scanned ⊇ hashed ⊇ (uploaded+deduped)
 * ⊇ uploaded, all % of the same dataset (totalBytes) — so it never jumps, and at completion the
 * uploaded + deduped bands together fill the track (the deduped band is the data that was NOT uploaded
 * because it deduplicated, so the bar reads as 100% done rather than stalling ~5% short). Restore is two
 * overlapping phases: hydration fill (chunk-space over chunksTotal) and restored fill (byte-space over
 * restoreTotalBytes), each independently monotonic 0→100; restore has no dedup band. Archive is a 4-stop
 * blue ramp (dedup band a distinct mid-blue); restore purples.
 */
@Component({
  selector: 'arius-layered-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div data-testid="layered-bar" style="position:relative;border-radius:999px;overflow:hidden"
         [style.height.px]="height()" [style.background]="'#eef0f3'">
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(scanned())" [style.background]="palette()[0]"></div>
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(middle())"  [style.background]="palette()[1]"></div>
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(deduped())" [style.background]="palette()[2]"></div>
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(top())"     [style.background]="palette()[3]"></div>
    </div>
  `,
})
export class LayeredBarComponent {
  readonly kind = input<'archive' | 'restore'>('archive');
  readonly height = input(14);
  readonly scanned = input(0);
  readonly middle = input(0);
  /** Archive only: cumulative % of (uploaded + deduplicated) bytes — the band from `top` to here is the
   *  deduplicated data that was never uploaded, so the bar can read as complete. 0 (unused) for restore. */
  readonly deduped = input(0);
  readonly top = input(0);
  // Archive is a 4-stop blue ramp painted darkest→lightest left-to-right (uploaded → deduped → hashed → scanned):
  // the dedup band is a distinct mid-blue (#60a5fa), NOT green, so it reads as part of the same family.
  protected readonly palette = computed<[string, string, string, string]>(() =>
    this.kind() === 'restore' ? ['#ede9fe', '#c4b5fd', '#ddd6fe', '#7c3aed'] : ['#dbeafe', '#93c5fd', '#60a5fa', '#2563eb']);
  protected clamp(n: number): number { return Math.max(0, Math.min(100, n)); }
}
