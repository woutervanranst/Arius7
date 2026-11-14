import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api/api.service';
import { GlobalDefaultsDto } from '../../core/api/api-models';
import { fromFactory, SetValues } from '../../core/defaults/defaults';
import { DefaultsEditorComponent } from '../../shared/defaults-editor/defaults-editor.component';

/** Settings: the global defaults every repository inherits (Factory -> Global settings level). */
@Component({
  selector: 'arius-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DefaultsEditorComponent],
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">Settings</h1>
    <p style="font-size:13.5px;color:#71717a;margin-top:2px">Global defaults for every repository. A repository can set its own in its Properties, and any run can change them.</p>
    @if (defaults(); as d) {
      <arius-defaults-editor level="settings" [saved]="d.set" [inherited]="inherited()!" [inheritedBy]="d.inheritedBy" (save)="save($event)" />
      @if (saveError()) { <div data-testid="defaults-save-error" style="color:#dc2626;font-size:12.5px;margin-top:8px">{{ saveError() }}</div> }
    } @else if (loadError()) {
      <div style="padding:24px 0;font-size:13px">
        <span data-testid="defaults-load-error" style="color:#dc2626">Could not load defaults.</span>
        <button class="ar-btn-outline" data-testid="defaults-load-retry" style="margin-left:10px" (click)="load()">Retry</button>
      </div>
    } @else {
      <div style="padding:24px 0;color:#a1a1aa;font-size:13px">Loading…</div>
    }
  `,
})
export class SettingsComponent {
  private readonly api = inject(ApiService);
  protected readonly defaults = signal<GlobalDefaultsDto | null>(null);
  protected readonly inherited = computed(() => { const d = this.defaults(); return d ? fromFactory(d.factory) : null; });
  protected readonly saveError = signal<string | null>(null);
  protected readonly loadError = signal(false);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loadError.set(false);
    this.api.getGlobalDefaults().subscribe({
      next: d => this.defaults.set(d),
      error: () => this.loadError.set(true),
    });
  }

  protected save(set: SetValues): void {
    this.saveError.set(null);
    this.api.putGlobalDefaults(set).subscribe({
      next: d => this.defaults.set(d),
      error: () => this.saveError.set('Could not save defaults — please try again.'),
    });
  }
}
