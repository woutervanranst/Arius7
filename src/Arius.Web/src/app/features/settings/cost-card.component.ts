import { ChangeDetectionStrategy, Component, computed, effect, input, linkedSignal, output } from '@angular/core';
import { CostSettingsDto } from '../../core/api/api-models';
import { MAX_AMOUNT, parseAmount } from './cost-amount';

/** Settings card for the server-wide cost confirmation limit; an edit the host saves with the rest of the settings. */
@Component({
  selector: 'arius-cost-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ar-card ar-cost-card" data-testid="cost-settings">
      <div class="ar-title">Cost</div>
      <label class="ar-row">
        If all files are in an online tier (Hot/Cool/Cold), auto-accept costs below
        <input type="number" class="ar-num" data-testid="cost-auto-accept" min="0" [max]="max" step="0.01" [value]="text()" (input)="setAmount($event)" />
        EUR (0 = always ask)
      </label>
      @if (invalid()) { <div class="ar-error" data-testid="cost-auto-accept-error">Enter an amount from 0 to {{ max }} EUR, to the cent.</div> }
    </div>
  `,
  styles: [`
    :host { display:block }
    .ar-cost-card { padding:20px 24px;margin-top:18px }
    .ar-title { font-size:15.5px;font-weight:600;color:#18181b;margin-bottom:14px }
    .ar-row { display:flex;flex-wrap:wrap;align-items:center;gap:8px;font-size:13.5px;color:#3f3f46 }
    .ar-num { width:72px;height:32px;border:1px solid #e4e4e7;border-radius:8px;text-align:center;font-size:13px;font-weight:600;font-family:inherit }
    .ar-error { font-size:12px;color:#dc2626;margin-top:6px }
  `],
})
export class CostCardComponent {
  protected readonly max = MAX_AMOUNT;
  readonly value = input.required<CostSettingsDto>();
  /** Emits only valid settings; an amount field holding anything else is reported through `validChange` instead. */
  readonly changed = output<CostSettingsDto>();
  readonly validChange = output<boolean>();

  /** What the amount field holds; follows `value` except that an in-progress edit it already agrees with is kept as typed. */
  protected readonly text = linkedSignal<CostSettingsDto, string>({
    source: this.value,
    computation: (v, previous) => previous && parseAmount(previous.value) === v.autoAcceptBelow ? previous.value : String(v.autoAcceptBelow),
  });
  protected readonly invalid = computed(() => parseAmount(this.text()) === null);

  constructor() {
    effect(() => this.validChange.emit(!this.invalid()));
  }

  protected setAmount(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.text.set(text);
    const amount = parseAmount(text);
    if (amount !== null) this.changed.emit({ autoAcceptBelow: amount });
  }
}
