import { AfterViewChecked, ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';
import { LogLine } from '../../core/api/api-models';

const COLORS: Record<string, string> = {
  ok: '#86efac', warn: '#fcd34d', dedup: '#c4b5fd', meta: '#71717a', info: '#7dd3fc',
};

/** Dark, auto-scrolling streaming log (Jobs console + archive/restore drawers). */
@Component({
  selector: 'arius-live-console',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div #scroll style="background:#0b0b0f;border-radius:10px;padding:12px 14px;overflow-y:auto;font-family:var(--ar-font-mono);font-size:12px;line-height:1.55"
         [style.height.px]="height()">
      @for (line of lines(); track $index) {
        <div style="white-space:pre-wrap">
          <span style="color:#52525b">{{ line.ts }}</span>
          <span [style.color]="color(line.severity)"> {{ line.text }}</span>
        </div>
      } @empty {
        <div style="color:#52525b">Waiting for output…</div>
      }
    </div>
  `,
})
export class LiveConsoleComponent implements AfterViewChecked {
  readonly lines = input<LogLine[]>([]);
  readonly height = input<number>(300);
  private readonly scroll = viewChild<ElementRef<HTMLDivElement>>('scroll');

  ngAfterViewChecked(): void {
    const el = this.scroll()?.nativeElement;
    if (el) el.scrollTop = el.scrollHeight;
  }

  protected color(severity: string): string {
    return COLORS[severity] ?? '#d4d4d8';
  }
}
