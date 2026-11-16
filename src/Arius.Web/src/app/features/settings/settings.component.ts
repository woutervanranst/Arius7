import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import { DownloadSettingsDto, GlobalDefaultsDto } from '../../core/api/api-models';
import { fromFactory, SetValues } from '../../core/defaults/defaults';
import { DefaultsEditorComponent } from '../../shared/defaults-editor/defaults-editor.component';
import { TemporaryFilesCardComponent } from './temporary-files-card.component';

/** Settings: the global defaults every repository inherits (Factory -> Global settings level) and the server-wide download clean-up. */
@Component({
  selector: 'arius-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DefaultsEditorComponent, TemporaryFilesCardComponent],
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">Settings</h1>
    @if (loadError()) {
      <div style="padding:24px 0;font-size:13px">
        <span data-testid="defaults-load-error" style="color:#dc2626">Could not load defaults.</span>
        <button class="ar-btn-outline" data-testid="defaults-load-retry" style="margin-left:10px" (click)="load()">Retry</button>
      </div>
    } @else if (defaults(); as d) {
      @if (downloadDraft(); as dl) {
        <arius-defaults-editor level="settings" [saved]="d.set" [inherited]="inherited()!" [inheritedBy]="d.inheritedBy"
          [extraDirty]="downloadDirty() || downloadInvalid()" [saveBlocked]="downloadInvalid()" (save)="save($event)" (discarded)="discardDownload()">
          <arius-temporary-files-card [value]="dl" (changed)="downloadDraft.set($event)" (validChange)="downloadInvalid.set(!$event)" />
        </arius-defaults-editor>
        @if (saveError()) { <div data-testid="defaults-save-error" style="color:#dc2626;font-size:12.5px;margin-top:8px">{{ saveError() }}</div> }
      } @else {
        <div style="padding:24px 0;color:#a1a1aa;font-size:13px">Loading…</div>
      }
    } @else {
      <div style="padding:24px 0;color:#a1a1aa;font-size:13px">Loading…</div>
    }
  `,
})
export class SettingsComponent {
  private readonly api = inject(ApiService);
  protected readonly defaults = signal<GlobalDefaultsDto | null>(null);
  protected readonly inherited = computed(() => { const d = this.defaults(); return d ? fromFactory(d.factory) : null; });
  private readonly downloadSaved = signal<DownloadSettingsDto | null>(null);
  protected readonly downloadDraft = signal<DownloadSettingsDto | null>(null);
  protected readonly downloadDirty = computed(() => {
    const saved = this.downloadSaved();
    const draft = this.downloadDraft();
    return !!saved && !!draft && (saved.cleanupEnabled !== draft.cleanupEnabled || saved.cleanupDays !== draft.cleanupDays);
  });
  protected readonly downloadInvalid = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly loadError = signal(false);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loadError.set(false);
    this.defaults.set(null);
    this.downloadDraft.set(null);
    this.api.getGlobalDefaults().subscribe({
      next: d => this.defaults.set(d),
      error: () => this.loadError.set(true),
    });
    this.api.getDownloadSettings().subscribe({
      next: s => this.setDownload(s),
      error: () => this.loadError.set(true),
    });
  }

  protected save(set: SetValues): void {
    this.saveError.set(null);
    forkJoin({
      defaults: this.api.putGlobalDefaults(set),
      download: this.downloadDirty() ? this.api.putDownloadSettings(this.downloadDraft()!) : of(null),
    }).subscribe({
      next: ({ defaults, download }) => {
        this.defaults.set(defaults);
        if (download) this.setDownload(download);
      },
      error: () => this.saveError.set('Could not save settings — please try again.'),
    });
  }

  protected discardDownload(): void {
    const saved = this.downloadSaved();
    this.downloadDraft.set(saved && { ...saved });
  }

  private setDownload(s: DownloadSettingsDto): void {
    this.downloadSaved.set(s);
    this.downloadDraft.set(s);
  }
}
