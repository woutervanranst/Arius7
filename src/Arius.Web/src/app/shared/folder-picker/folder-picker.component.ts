import { ChangeDetectionStrategy, Component, inject, input, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api/api.service';
import { FsEntryDto } from '../../core/api/api-models';

/**
 * Local-path input with a [...] button that browses directories AS THE Arius.Api HOST/CONTAINER SEES THEM
 * (mounted volumes under Docker; real folders in dev) — the path must resolve on the API side, not the
 * browser's, so the picker is server-driven via /api/fs/list. The text input stays editable for power users.
 */
@Component({
  selector: 'arius-folder-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    <div style="position:relative">
      <div class="flex items-center" style="gap:8px">
        <input class="ar-input ar-mono" style="flex:1" [ngModel]="value()" (ngModelChange)="value.set($event)"
               [attr.data-testid]="testid() || null" [placeholder]="placeholder()" />
        <button type="button" class="ar-fp-browse" data-testid="folder-browse" (click)="toggle()" title="Browse folders on the Arius.Api host">…</button>
      </div>

      @if (open()) {
        <div class="ar-fp-backdrop" (click)="open.set(false)"></div>
        <div class="ar-fp-pop" data-testid="folder-picker">
          <div class="ar-fp-path ar-mono" data-testid="folder-current">{{ browsePath() || '/' }}</div>
          <div class="ar-fp-list">
            @if (parent() !== null) {
              <button type="button" class="ar-fp-item" (click)="browse(parent())"><i class="ki-filled ki-up"></i> ..</button>
            }
            @for (e of entries(); track e.path) {
              <button type="button" class="ar-fp-item" data-testid="folder-entry" (click)="browse(e.path)">
                <i class="ki-filled ki-folder"></i> {{ e.name }}
              </button>
            } @empty {
              @if (!loading() && !error()) { <div class="ar-fp-empty">No subfolders here.</div> }
            }
            @if (loading()) { <div class="ar-fp-empty">Loading…</div> }
            @if (error()) { <div class="ar-fp-error">{{ error() }}</div> }
          </div>
          <div class="ar-fp-foot">
            <button type="button" class="ar-btn-outline" (click)="open.set(false)">Cancel</button>
            <button type="button" class="ar-btn-primary" data-testid="folder-use" [disabled]="loading()" (click)="use()">Use this folder</button>
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    .ar-fp-browse { width:40px;height:40px;flex-shrink:0;border:1px solid #e4e4e7;border-radius:9px;background:#fff;color:#52525b;font-size:18px;line-height:1 }
    .ar-fp-browse:hover { border-color:#3b82f6;color:#3b82f6 }
    .ar-fp-backdrop { position:fixed;inset:0;z-index:50 }
    .ar-fp-pop { position:absolute;z-index:51;top:46px;left:0;right:0;background:#fff;border:1px solid #e4e4e7;border-radius:11px;box-shadow:0 12px 32px rgba(9,9,11,.16);overflow:hidden }
    .ar-fp-path { padding:10px 12px;font-size:12px;color:#71717a;background:#f7f7f8;border-bottom:1px solid #f0f0f2;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;direction:rtl;text-align:left }
    .ar-fp-list { max-height:240px;overflow-y:auto;padding:4px }
    .ar-fp-item { display:flex;align-items:center;gap:8px;width:100%;text-align:left;padding:8px 10px;border-radius:7px;font-size:13px;color:#27272a }
    .ar-fp-item:hover { background:#f4f4f5 }
    .ar-fp-item > i { color:#a1a1aa }
    .ar-fp-empty { padding:12px;font-size:12.5px;color:#a1a1aa }
    .ar-fp-error { padding:12px;font-size:12.5px;color:#dc2626 }
    .ar-fp-foot { display:flex;justify-content:flex-end;gap:8px;padding:10px 12px;border-top:1px solid #f0f0f2 }
    .ar-input { width:100%;height:40px;border:1px solid #e4e4e7;border-radius:9px;padding:0 12px;font-size:13.5px;outline:none }
    .ar-input:focus { border-color:#3b82f6 }
  `],
})
export class FolderPickerComponent {
  private readonly api = inject(ApiService);

  /** Two-way bound selected path (the path as the API host sees it). */
  readonly value = model<string>('');
  readonly placeholder = input<string>('');
  /** data-testid forwarded to the text input (so existing/e2e selectors can target it). */
  readonly testid = input<string>('');

  protected readonly open = signal(false);
  protected readonly browsePath = signal<string>('');
  protected readonly parent = signal<string | null>(null);
  protected readonly entries = signal<FsEntryDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  // Monotonic id of the in-flight browse: a slower earlier response is ignored so rapid
  // click-through can't let a stale folder listing overwrite a newer one.
  private browseSeq = 0;

  protected toggle(): void {
    if (this.open()) { this.open.set(false); return; }
    this.open.set(true);
    this.browse(this.value() || null); // start from the current value, or the server's default root
  }

  protected browse(path: string | null): void {
    const seq = ++this.browseSeq;
    this.loading.set(true);
    this.error.set(null);
    this.api.listDirectories(path).subscribe({
      next: r => {
        if (seq !== this.browseSeq) return; // a newer browse has superseded this response
        this.browsePath.set(r.path); this.parent.set(r.parent); this.entries.set(r.entries); this.loading.set(false);
      },
      error: () => {
        if (seq !== this.browseSeq) return;
        this.error.set('Could not read that folder on the Arius.Api host.'); this.loading.set(false);
      },
    });
  }

  protected use(): void {
    this.value.set(this.browsePath());
    this.open.set(false);
  }
}
