import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { ApiService } from '../../core/api/api.service';
import { DownloadSettingsDto, DownloadUsageDto } from '../../core/api/api-models';
import { formatBytes } from '../../shared/format';

/**
 * Settings card for the server-wide clean-up of downloaded files. The clean-up period is an edit the host saves
 * with the rest of the settings; "Clean up all now" acts immediately and is not part of Save.
 */
@Component({
  selector: 'arius-temporary-files-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ar-card ar-temp-card" data-testid="temporary-files">
      <div class="ar-title">Temporary files</div>
      <p class="ar-desc">Downloading collected files saves them in a temporary location on the server before your browser downloads them.</p>
      <label class="ar-switch-row">
        <input type="checkbox" role="switch" class="ar-switch" data-testid="cleanup-toggle" [checked]="value().cleanupEnabled" (change)="toggle($event)" />
        Clean up downloaded files after
        <input type="number" class="ar-num" data-testid="cleanup-days" min="1" [value]="value().cleanupDays" [disabled]="!value().cleanupEnabled" (input)="setDays($event)" />
        days
      </label>
      <div class="ar-hint">Off keeps them on the server until you clean them up.</div>
      <div class="ar-usage">
        <div class="ar-usage-text">
          <div class="ar-usage-label">Currently on the server</div>
          @if (usage(); as u) {
            <div class="ar-usage-value" data-testid="download-usage">{{ u.downloads }} {{ u.downloads === 1 ? 'download' : 'downloads' }} · {{ size(u) }}</div>
          }
        </div>
        <button type="button" class="ar-btn-outline ar-danger-outline" data-testid="cleanup-all" (click)="cleanUpAll()"><i class="ki-filled ki-trash"></i>Clean up all now</button>
      </div>
    </div>
  `,
  styles: [`
    :host { display:block }
    .ar-temp-card { padding:20px 24px;margin-top:18px }
    .ar-title { font-size:15.5px;font-weight:600;color:#18181b }
    .ar-desc { font-size:12.5px;color:#a1a1aa;margin:2px 0 16px }
    .ar-switch-row { display:flex;align-items:center;gap:10px;font-size:13.5px;color:#3f3f46 }
    .ar-switch { appearance:none;flex:none;position:relative;width:34px;height:20px;border-radius:999px;background:#d4d4d8;cursor:pointer;transition:background .15s }
    .ar-switch::after { content:'';position:absolute;top:2px;left:2px;width:16px;height:16px;border-radius:50%;background:#fff;transition:transform .15s }
    .ar-switch:checked { background:#3b82f6 }
    .ar-switch:checked::after { transform:translateX(14px) }
    .ar-num { width:56px;height:32px;border:1px solid #e4e4e7;border-radius:8px;text-align:center;font-size:13px;font-weight:600;font-family:inherit }
    .ar-num:disabled { background:#fafafa;color:#a1a1aa }
    .ar-hint { font-size:12px;color:#71717a;margin:6px 0 16px }
    .ar-usage { display:flex;align-items:center;gap:14px;background:#fafafb;border:1px solid #f0f0f2;border-radius:10px;padding:12px 14px }
    .ar-usage-text { flex:1 }
    .ar-usage-label { font-size:13px;font-weight:600;color:#3f3f46 }
    .ar-usage-value { font-size:12.5px;color:#71717a;margin-top:2px }
    .ar-danger-outline { border-color:#fecaca;color:#dc2626 }
    .ar-danger-outline:hover { background:#fef2f2 }
  `],
})
export class TemporaryFilesCardComponent {
  private readonly api = inject(ApiService);

  readonly value = input.required<DownloadSettingsDto>();
  readonly changed = output<DownloadSettingsDto>();

  protected readonly usage = signal<DownloadUsageDto | null>(null);

  constructor() {
    this.api.getDownloadUsage().subscribe({ next: u => this.usage.set(u) });
  }

  protected size(u: DownloadUsageDto): string {
    return formatBytes(u.bytes);
  }

  protected toggle(event: Event): void {
    this.changed.emit({ ...this.value(), cleanupEnabled: (event.target as HTMLInputElement).checked });
  }

  protected setDays(event: Event): void {
    this.changed.emit({ ...this.value(), cleanupDays: Number((event.target as HTMLInputElement).value) });
  }

  protected cleanUpAll(): void {
    this.api.cleanUpAllDownloads().subscribe({ next: u => this.usage.set(u) });
  }
}
