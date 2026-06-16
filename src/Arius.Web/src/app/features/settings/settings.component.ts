import { ChangeDetectionStrategy, Component } from '@angular/core';
import { PlaceholderComponent } from '../placeholder.component';

@Component({
  selector: 'arius-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PlaceholderComponent],
  template: `<arius-placeholder title="Settings" />`,
})
export class SettingsComponent {}
