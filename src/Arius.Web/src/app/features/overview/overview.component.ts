import { ChangeDetectionStrategy, Component } from '@angular/core';
import { StateRingComponent } from '../../shared/state-ring/state-ring.component';

/** Overview placeholder — also a live gallery of the state ring (verifies tokens, ring, Keenicons). */
@Component({
  selector: 'arius-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StateRingComponent],
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">Overview</h1>
    <p style="font-size:13.5px;color:#71717a;margin-top:2px">
      Foundation scaffolded — repositories, jobs and the file browser are wired in the next phase.
    </p>

    <div class="ar-card" style="margin-top:24px;padding:20px 22px;max-width:680px">
      <div style="font-size:15.5px;font-weight:600;color:#18181b">State ring</div>
      <p style="font-size:13px;color:#71717a;margin:4px 0 18px">
        One disc per file — left half = local disk, right half = repository.
      </p>
      <div style="display:flex;flex-wrap:wrap;gap:26px">
        @for (sample of samples; track sample.label) {
          <div style="display:flex;flex-direction:column;align-items:center;gap:8px;width:96px;text-align:center">
            <arius-state-ring [state]="sample.state" [size]="40" />
            <span style="font-size:11px;color:#52525b">{{ sample.label }}</span>
          </div>
        }
      </div>
    </div>
  `,
})
export class OverviewComponent {
  // Flag combinations from the design's state table (see RepositoryEntryState).
  protected readonly samples = [
    { label: 'In sync', state: 1 | 2 | 8 | 16 },
    { label: 'Pointer only', state: 1 | 8 | 16 },
    { label: 'Archive tier', state: 1 | 8 | 32 },
    { label: 'Rehydrating', state: 1 | 8 | 32 | 64 },
    { label: 'Not archived', state: 2 },
    { label: 'In repo only', state: 8 | 16 },
  ];
}
