import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { DefaultValues, OnDisk, OptionKey, Pill, Tier } from '../../core/defaults/defaults';

interface FieldCopy {
  label: string;
  /** Switch options only: the caption beside the switch and the testid it carries. */
  caption?: string;
  testid?: string;
  /** Switch options: hint for the on / off value. */
  hintOn?: string;
  hintOff?: string;
}

const COPY: Record<OptionKey, FieldCopy> = {
  tier: { label: 'Upload tier' },
  onDisk: { label: 'On disk after archive' },
  fastHash: {
    label: 'Fast hash', caption: 'Skip re-reading unchanged files', testid: 'toggle-fast-hash',
    hintOn: 'Trusts the local hash cache for files whose size and timestamps are unchanged. Much faster on large folders.',
    hintOff: 'Every file is fully re-read and hashed. Slower, but the safest.',
  },
  overwrite: {
    label: 'Overwrite existing files', caption: 'Replace local files that differ from the snapshot', testid: 'toggle-overwrite',
    hintOn: 'Local files that differ are overwritten with the archived version.',
    hintOff: 'Local files that already exist are left untouched.',
  },
  noPointers: {
    label: 'Skip pointer files', caption: "Don't write .pointer.arius files", testid: 'toggle-no-pointers',
    hintOn: 'Only the restored files are written.',
    hintOff: 'A pointer file is written next to each restored file.',
  },
};

const TIERS: { value: Tier; label: string; hint: string }[] = [
  { value: 'hot', label: 'Hot', hint: 'Instant reads · highest storage cost · no minimum retention' },
  { value: 'cool', label: 'Cool', hint: 'Instant reads · lower storage cost · 30-day minimum' },
  { value: 'cold', label: 'Cold', hint: 'Instant reads · lower still · 90-day minimum' },
  { value: 'archive', label: 'Archive', hint: 'Cheapest storage · restores need rehydration (hours) · 180-day minimum' },
];

export const ON_DISK: { value: OnDisk; label: string; hint: string }[] = [
  { value: 'keep', label: 'Keep files only', hint: 'Local files stay as they are.' },
  { value: 'keep-pointers', label: 'Keep files + pointers', hint: 'Also writes a .pointer.arius file next to each archived file.' },
  { value: 'replace', label: 'Replace with pointers', hint: '' },
];

/**
 * One option of the archive/restore defaults hierarchy: label, provenance pill (+ Reset), control, hint
 * and — for `onDisk = replace` — the destructive warning. Purely presentational; the host owns the value.
 */
@Component({
  selector: 'arius-default-field',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-option]': 'option()' },
  template: `
    <div class="ar-label">
      <span>{{ copy().label }}</span>
      <span class="ar-pill" [class.set]="pill().canReset" data-testid="default-source">{{ pill().text }}</span>
      @if (pill().canReset) {
        <button type="button" class="ar-reset" data-testid="default-reset" (click)="reset.emit()">Reset</button>
      }
    </div>

    @if (segments(); as segs) {
      <div class="ar-seg">
        @for (s of segs; track s.value) {
          @if (option() === 'tier') {
            <button type="button" data-testid="tier-seg" [attr.data-tier]="s.value" [class.on]="value() === s.value" (click)="picked.emit(s.value)">{{ s.label }}</button>
          } @else {
            <button type="button" data-testid="seg-on-disk" [attr.data-on-disk]="s.value" [class.on]="value() === s.value" (click)="picked.emit(s.value)">{{ s.label }}</button>
          }
        }
      </div>
    } @else {
      <label class="ar-switch-row">
        <input type="checkbox" role="switch" class="ar-switch" [attr.data-testid]="copy().testid" [checked]="value() === true" (change)="picked.emit(value() !== true)" />
        {{ copy().caption }}
      </label>
    }

    @if (value() === 'replace') {
      <div class="ar-warn" data-testid="default-warning">
        <b>Deletes local files.</b> Each file is removed once it is safely archived, leaving only its pointer file.
        @if (note()) { {{ note() }} }
      </div>
    } @else if (hint()) {
      <div class="ar-hint">{{ hint() }}</div>
    }
  `,
  styles: [`
    :host { display:block;margin-bottom:16px }
    .ar-label { display:flex;align-items:center;gap:8px;margin-bottom:7px }
    .ar-label > span:first-child { font-size:13px;font-weight:600;color:#3f3f46 }
    .ar-pill { font-size:11px;font-weight:500;border-radius:999px;padding:1px 8px;background:#f4f4f5;color:#71717a }
    .ar-pill.set { background:#fef3c7;color:#92400e }
    .ar-reset { font-size:11.5px;font-weight:600;color:#3b82f6;margin-left:auto }
    .ar-seg { display:flex;gap:8px }
    .ar-seg > button { flex:1;height:40px;border-radius:9px;border:1.5px solid #e4e4e7;background:#fff;color:#52525b;font-size:13px;font-weight:600 }
    .ar-seg > button.on { border-color:#3b82f6;background:#eff4ff;color:#3b82f6 }
    .ar-switch-row { display:flex;align-items:center;gap:10px;font-size:13.5px;color:#3f3f46 }
    .ar-switch { appearance:none;flex:none;position:relative;width:34px;height:20px;border-radius:999px;background:#d4d4d8;cursor:pointer;transition:background .15s }
    .ar-switch::after { content:'';position:absolute;top:2px;left:2px;width:16px;height:16px;border-radius:50%;background:#fff;transition:transform .15s }
    .ar-switch:checked { background:#3b82f6 }
    .ar-switch:checked::after { transform:translateX(14px) }
    .ar-hint { font-size:12px;color:#71717a;margin-top:6px }
    .ar-warn { font-size:12.5px;color:#92400e;background:#fffbeb;border:1px solid #fde68a;border-radius:9px;padding:10px 12px;margin-top:8px }
  `],
})
export class DefaultFieldComponent {
  readonly option = input.required<OptionKey>();
  readonly value = input.required<DefaultValues[OptionKey]>();
  readonly pill = input.required<Pill>();
  readonly note = input('');

  readonly picked = output<DefaultValues[OptionKey]>();
  readonly reset = output<void>();

  protected readonly copy = computed(() => COPY[this.option()]);
  protected readonly segments = computed(() => {
    const option = this.option();
    return option === 'tier' ? TIERS : option === 'onDisk' ? ON_DISK : null;
  });
  protected readonly hint = computed(() => {
    const option = this.option();
    const value = this.value();
    if (option === 'tier') return TIERS.find(t => t.value === value)?.hint ?? '';
    if (option === 'onDisk') return ON_DISK.find(t => t.value === value)?.hint ?? '';
    return value === true ? COPY[option].hintOn : COPY[option].hintOff;
  });
}
