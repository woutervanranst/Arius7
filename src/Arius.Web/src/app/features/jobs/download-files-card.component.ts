import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ApiService, downloadContentUrl } from '../../core/api/api.service';
import { DownloadFileDto, isNonTerminal } from '../../core/api/api-models';
import { NotificationService } from '../../core/services/notification.service';
import { formatBytes } from '../../shared/format';
import { DownloadAction, downloadActions, fileStatus, readyHeadline, readySubline, summarize } from './download-files';

const RELOAD_THROTTLE_MS = 1000;

/**
 * The download job's files card: a green summary (ready count, clean-up note, actions on one line) above the
 * file table. Downloads are plain `<a href download>` links to the server — never fetched by JS. The file list is
 * reloaded whenever the job's status or restored-file count changes; the server stays the truth for "downloaded".
 */
@Component({
  selector: 'arius-download-files-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (files().length > 0) {
      <div data-testid="download-card" style="margin-top:20px">
        @if (showSummary()) {
          <div data-testid="download-summary" style="display:flex;align-items:center;gap:12px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:12px;padding:14px 18px;flex-wrap:wrap;margin-bottom:12px">
            <div style="width:38px;height:38px;border-radius:10px;background:#dcfce7;color:#15803d;display:flex;align-items:center;justify-content:center;flex-shrink:0">
              <i class="ki-filled ki-exit-down" style="font-size:19px"></i>
            </div>
            <div style="flex:1;min-width:180px">
              @if (cleanedUp()) {
                <div style="font-size:13.5px;font-weight:600;color:#14532d">Files cleaned up</div>
              } @else {
                <div style="font-size:13.5px;font-weight:600;color:#14532d">{{ headline() }}</div>
                @if (subline()) { <div style="font-size:12px;color:#15803d;margin-top:2px">{{ subline() }}</div> }
              }
            </div>
            @if (!cleanedUp()) {
              <div style="display:flex;align-items:center;gap:8px;flex-wrap:nowrap;margin-left:auto">
                @if (finished()) {
                  <button data-testid="download-cleanup" type="button" (click)="cleanUp()"
                          style="background:none;border:none;padding:0 6px;font-size:12.5px;font-weight:600;color:#71717a;cursor:pointer;white-space:nowrap">Clean up now</button>
                }
                @for (a of actions(); track a.testid) {
                  <a [href]="url(a)" download [attr.data-testid]="a.testid" (click)="markDownloaded(a)"
                     style="display:inline-flex;align-items:center;gap:7px;height:34px;padding:0 12px;border-radius:9px;font-size:12.5px;font-weight:600;text-decoration:none;white-space:nowrap;cursor:pointer"
                     [style.background]="a.primary ? '#7c3aed' : '#fff'"
                     [style.color]="a.primary ? '#fff' : '#6d28d9'"
                     [style.border]="a.primary ? '1px solid #7c3aed' : '1px solid #ddd6fe'">
                    <i class="ki-filled ki-exit-down" style="font-size:14px"></i>{{ a.label }}
                  </a>
                }
              </div>
            }
          </div>
        }

        <div style="border:1px solid #ececef;border-radius:11px;overflow:hidden;font-size:12.5px">
          <div style="display:grid;grid-template-columns:1fr 90px 120px;padding:8px 14px;background:#fafafb;font-size:10.5px;font-weight:600;letter-spacing:.05em;text-transform:uppercase;color:#a1a1aa">
            <span>File</span><span>Size</span><span>Status</span>
          </div>
          @for (f of files(); track f.path) {
            <div data-testid="download-file-row" style="display:grid;grid-template-columns:1fr 90px 120px;padding:8px 14px;border-top:1px solid #f4f4f5;align-items:center">
              <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#27272a" [attr.title]="f.path">{{ f.path }}</span>
              <span style="color:#71717a">{{ formatBytes(f.size) }}</span>
              <span [style.color]="statusColor(f)">{{ statusOf(f) }}</span>
            </div>
          }
        </div>
      </div>
    }
  `,
})
export class DownloadFilesCardComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotificationService);

  readonly jobId = input.required<string>();
  readonly status = input.required<string>();
  /** Not read; a change re-triggers the load so newly restored files turn ready without a status change. */
  readonly filesRestored = input<number>(0);

  protected readonly files = signal<DownloadFileDto[]>([]);
  protected readonly cleanedUp = signal(false);
  private readonly cleanupAt = signal<string | null>(null);
  private load: Subscription | null = null;
  private inFlight = false;
  private reloadAgain = false;
  private trailing: ReturnType<typeof setTimeout> | null = null;

  protected readonly finished = computed(() => !isNonTerminal(this.status()));
  private readonly summary = computed(() => summarize(this.files()));
  /** Hidden for a finished job where nothing ever became ready (e.g. cancelled at the cost prompt): there is nothing to offer. */
  protected readonly showSummary = computed(() => !this.finished() || this.summary().ready > 0);
  protected readonly headline = computed(() => readyHeadline(this.summary(), this.finished()));
  protected readonly subline = computed(() => readySubline(this.summary(), this.status(), this.cleanupAt()));
  protected readonly actions = computed(() => downloadActions(this.summary(), this.finished()));

  protected readonly formatBytes = formatBytes;

  constructor() {
    // A job or status change reloads promptly; progress ticks (filesRestored) are coalesced into one trailing reload.
    let last: { id: string; status: string } | null = null;
    effect(() => {
      const id = this.jobId(), status = this.status();
      this.filesRestored();
      if (last?.id !== id || last.status !== status) { last = { id, status }; this.reload(); }
      else if (this.trailing === null) this.trailing = setTimeout(() => { this.trailing = null; this.reload(); }, RELOAD_THROTTLE_MS);
    });
  }

  protected url(a: DownloadAction): string { return downloadContentUrl(this.jobId(), a.set); }

  /** Optimistic: the browser starts the transfer on click; the server records it once the response completes. */
  protected markDownloaded(a: DownloadAction): void {
    // Deferred so the actions list doesn't change before the anchor's default (download) action runs.
    setTimeout(() => this.files.update(list => list.map(f => f.ready && (a.set === 'all' || !f.downloaded) ? { ...f, downloaded: true } : f)), 0);
  }

  protected cleanUp(): void {
    this.api.cleanUpDownload(this.jobId()).subscribe({
      next: () => this.reload(),
      error: () => this.notify.error("Couldn't clean up the downloaded files — try again."),
    });
  }

  protected statusColor(f: DownloadFileDto): string {
    switch (this.statusOf(f)) {
      case 'Downloaded': return '#15803d';
      case 'Ready': return '#6d28d9';
      case 'Rehydrating': return '#b45309';
      default: return '#a1a1aa';
    }
  }
  protected statusOf(f: DownloadFileDto): string { return fileStatus(f, this.status(), this.cleanedUp()); }

  /** At most one request in flight; a trigger that arrives meanwhile is remembered and re-run once it answers. */
  private reload(): void {
    if (this.inFlight) { this.reloadAgain = true; return; }
    this.inFlight = true;
    const id = this.jobId();
    const done = () => { this.inFlight = false; if (this.reloadAgain) { this.reloadAgain = false; this.reload(); } };
    this.load = this.api.getDownload(id).subscribe({
      next: d => {
        if (this.jobId() === id) { this.files.set(d.files); this.cleanedUp.set(d.cleanedUp); this.cleanupAt.set(d.cleanupAt); }   // else the page moved to another job
        done();
      },
      error: () => done(),   // keep the last known list; the next status or progress change reloads
    });
  }

  ngOnDestroy(): void {
    this.load?.unsubscribe();
    if (this.trailing !== null) clearTimeout(this.trailing);
  }
}
