import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import { CostSettingsDto, DownloadSettingsDto, GlobalDefaultsDto } from '../../core/api/api-models';
import { fromFactory, SetValues } from '../../core/defaults/defaults';
import { DefaultsEditorComponent } from '../../shared/defaults-editor/defaults-editor.component';
import { CostCardComponent } from './cost-card.component';
import { TemporaryFilesCardComponent } from './temporary-files-card.component';

/** Settings: the global defaults every repository inherits (Factory -> Global settings level), the server-wide cost confirmation limit and download clean-up. */
@Component({
  selector: 'arius-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DefaultsEditorComponent, CostCardComponent, TemporaryFilesCardComponent],
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">Settings</h1>
    @if (loadError()) {
      <div style="padding:24px 0;font-size:13px">
        <span data-testid="defaults-load-error" style="color:#dc2626">Could not load defaults.</span>
        <button class="ar-btn-outline" data-testid="defaults-load-retry" style="margin-left:10px" (click)="load()">Retry</button>
      </div>
    } @else if (defaults(); as d) {
      @let dl = downloadDraft();
      @let cost = costDraft();
      @if (dl && cost) {
        <arius-defaults-editor level="settings" [saved]="d.set" [inherited]="inherited()!" [inheritedBy]="d.inheritedBy"
          [extraDirty]="downloadDirty() || costDirty() || saveBlocked()" [saveBlocked]="saveBlocked()" (save)="save($event)" (discarded)="discardExtras()">
          <arius-cost-card [value]="cost" (changed)="costDraft.set($event)" (validChange)="costInvalid.set(!$event)" />
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
  private readonly costSaved = signal<CostSettingsDto | null>(null);
  protected readonly costDraft = signal<CostSettingsDto | null>(null);
  protected readonly costDirty = computed(() => {
    const saved = this.costSaved();
    const draft = this.costDraft();
    return !!saved && !!draft && saved.autoAcceptBelow !== draft.autoAcceptBelow;
  });
  protected readonly costInvalid = signal(false);
  protected readonly saveBlocked = computed(() => this.downloadInvalid() || this.costInvalid());
  protected readonly saveError = signal<string | null>(null);
  protected readonly loadError = signal(false);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loadError.set(false);
    this.defaults.set(null);
    this.downloadDraft.set(null);
    this.costDraft.set(null);
    this.api.getGlobalDefaults().subscribe({
      next: d => this.defaults.set(d),
      error: () => this.loadError.set(true),
    });
    this.api.getDownloadSettings().subscribe({
      next: s => this.setDownload(s),
      error: () => this.loadError.set(true),
    });
    this.api.getCostSettings().subscribe({
      next: s => this.setCost(s),
      error: () => this.loadError.set(true),
    });
  }

  protected save(set: SetValues): void {
    this.saveError.set(null);
    forkJoin({
      defaults: this.api.putGlobalDefaults(set),
      download: this.downloadDirty() ? this.api.putDownloadSettings(this.downloadDraft()!) : of(null),
      cost: this.costDirty() ? this.api.putCostSettings(this.costDraft()!) : of(null),
    }).subscribe({
      next: ({ defaults, download, cost }) => {
        this.defaults.set(defaults);
        if (download) this.setDownload(download);
        if (cost) this.setCost(cost);
      },
      error: () => this.saveError.set('Could not save settings — please try again.'),
    });
  }

  protected discardExtras(): void {
    const download = this.downloadSaved();
    this.downloadDraft.set(download && { ...download });
    const cost = this.costSaved();
    this.costDraft.set(cost && { ...cost });
  }

  private setDownload(s: DownloadSettingsDto): void {
    this.downloadSaved.set(s);
    this.downloadDraft.set(s);
  }

  private setCost(s: CostSettingsDto): void {
    this.costSaved.set(s);
    this.costDraft.set(s);
  }
}
