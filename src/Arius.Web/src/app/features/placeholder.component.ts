import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Temporary placeholder for screens wired in later phases. */
@Component({
  selector: 'arius-placeholder',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 class="ar-heading" style="font-size:22px;font-weight:700">{{ title() }}</h1>
    <p style="font-size:13.5px;color:#71717a;margin-top:2px">{{ note() }}</p>
  `,
})
export class PlaceholderComponent {
  readonly title = input('');
  readonly note = input('Coming in a later phase.');
}
