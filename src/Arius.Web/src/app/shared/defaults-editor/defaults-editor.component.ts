import { ChangeDetectionStrategy, Component, computed, input, linkedSignal, output } from '@angular/core';
import {
  ARCHIVE_OPTIONS, DefaultValues, OptionKey, RESTORE_OPTIONS, ResolvedDefaults, SetValues,
  levelPill, replaceNote, sameSet, withPick, without,
} from '../../core/defaults/defaults';
import { DefaultFieldComponent } from '../default-field/default-field.component';

const COPY = {
  settings: { archive: 'Used when archiving, scheduled runs included.', restore: 'Used when restoring.' },
  repository: { archive: "For this repository's archives, scheduled runs included.", restore: 'For restores from this repository.' },
} as const;

/**
 * Editor for one level of the defaults hierarchy (global settings or repository properties): an Archive and a
 * Restore card of `arius-default-field`s over a local draft, with one Save / Discard bar. The host persists.
 */
@Component({
  selector: 'arius-defaults-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DefaultFieldComponent],
  template: `
    @for (card of cards(); track card.testid) {
      <div class="ar-card ar-defaults-card" [attr.data-testid]="card.testid">
        <div class="ar-title">{{ card.title }}</div>
        <p class="ar-desc">{{ card.description }}</p>
        @for (key of card.options; track key) {
          <arius-default-field [option]="key" [value]="valueOf(key)" [pill]="pillOf(key)" [note]="key === 'onDisk' ? onDiskNote() : ''"
            (picked)="pick(key, $event)" (reset)="unset(key)" />
        }
      </div>
    }
    <div class="ar-bar">
      @if (dirty()) { <span class="ar-dirty">Unsaved changes</span> }
      <button class="ar-btn-outline" data-testid="defaults-discard" [disabled]="!dirty()" (click)="draft.set(saved())">Discard</button>
      <button class="ar-btn-primary" data-testid="defaults-save" [disabled]="!dirty()" (click)="save.emit(draft())"><i class="ki-filled ki-check"></i>Save defaults</button>
    </div>
  `,
  styles: [`
    :host { display:block;max-width:680px }
    .ar-defaults-card { padding:20px 24px;margin-top:18px }
    .ar-title { font-size:15.5px;font-weight:600;color:#18181b }
    .ar-desc { font-size:12.5px;color:#a1a1aa;margin:2px 0 16px }
    .ar-bar { display:flex;align-items:center;justify-content:flex-end;gap:10px;margin-top:18px }
    .ar-dirty { font-size:12.5px;color:#92400e;margin-right:auto }
  `],
})
export class DefaultsEditorComponent {
  readonly level = input.required<'settings' | 'repository'>();
  readonly saved = input.required<SetValues>();
  readonly inherited = input.required<ResolvedDefaults>();
  readonly inheritedBy = input<Record<OptionKey, string[]>>();

  readonly save = output<SetValues>();

  /**
   * Edits in flight; follows `saved` whenever the host hands a new one (after a save, or a reload) —
   * except that edits made while a save was in flight are kept rather than overwritten by its response.
   */
  protected readonly draft = linkedSignal<SetValues, SetValues>({
    source: this.saved,
    computation: (saved, previous) =>
      previous && !sameSet(previous.value, previous.source) && !sameSet(previous.value, saved) ? previous.value : saved,
  });
  protected readonly dirty = computed(() => !sameSet(this.draft(), this.saved()));
  protected readonly onDiskNote = computed(() => replaceNote(this.level(), this.inheritedBy()?.onDisk));
  protected readonly cards = computed(() => {
    const copy = COPY[this.level()];
    return [
      { testid: 'defaults-archive', title: 'Archive defaults', description: copy.archive, options: ARCHIVE_OPTIONS },
      { testid: 'defaults-restore', title: 'Restore defaults', description: copy.restore, options: RESTORE_OPTIONS },
    ];
  });

  protected valueOf(key: OptionKey): DefaultValues[OptionKey] {
    return this.draft()[key] ?? this.inherited()[key].value;
  }

  protected pillOf(key: OptionKey) {
    return levelPill(this.level(), key, this.draft(), this.inherited());
  }

  protected pick(key: OptionKey, value: DefaultValues[OptionKey]): void {
    this.draft.update(d => withPick(d, key, value));
  }

  protected unset(key: OptionKey): void {
    this.draft.update(d => without(d, key));
  }
}
