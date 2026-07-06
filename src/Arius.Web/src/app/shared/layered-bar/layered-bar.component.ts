import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * Byte-weighted layered progress bar (design README §Screens 2). ONE track, three overlapping fills.
 * Archive: all three fills are % of the same dataset (totalBytes), each a subset of the previous
 * (scanned ⊇ hashed ⊇ uploaded) — so it never jumps or hangs. Restore is two overlapping phases:
 * the hydration fill is chunk-space (over chunksTotal) and the restored fill is byte-space
 * (over restoreTotalBytes); each is independently monotonic 0→100. Archive palette blues, restore purples.
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
      <div style="position:absolute;inset:0;border-radius:999px;transition:width .3s" [style.width.%]="clamp(top())"     [style.background]="palette()[2]"></div>
    </div>
  `,
})
export class LayeredBarComponent {
  readonly kind = input<'archive' | 'restore'>('archive');
  readonly height = input(14);
  readonly scanned = input(0);
  readonly middle = input(0);
  readonly top = input(0);
  protected readonly palette = computed<[string, string, string]>(() =>
    this.kind() === 'restore' ? ['#ede9fe', '#c4b5fd', '#7c3aed'] : ['#dbeafe', '#93c5fd', '#2563eb']);
  protected clamp(n: number): number { return Math.max(0, Math.min(100, n)); }
}
