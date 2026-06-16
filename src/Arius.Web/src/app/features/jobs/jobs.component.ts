import { ChangeDetectionStrategy, Component } from '@angular/core';
import { PlaceholderComponent } from '../placeholder.component';

@Component({
  selector: 'arius-jobs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PlaceholderComponent],
  template: `<arius-placeholder title="Jobs" note="The jobs table and live console arrive with streaming archive/restore." />`,
})
export class JobsComponent {}
