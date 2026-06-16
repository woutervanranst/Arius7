import { ChangeDetectionStrategy, Component } from '@angular/core';
import { PlaceholderComponent } from '../placeholder.component';

@Component({
  selector: 'arius-repos',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PlaceholderComponent],
  template: `<arius-placeholder title="Repositories" note="The repository list and file browser arrive in the next phase." />`,
})
export class ReposComponent {}
