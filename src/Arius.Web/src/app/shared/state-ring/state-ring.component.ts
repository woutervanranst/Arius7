import { ChangeDetectionStrategy, Component, computed, input, ViewEncapsulation } from '@angular/core';
import { hasFlag, RepositoryEntryState as S } from './repository-entry-state';

export interface RingColors {
  /** left outer = pointer on disk */ lo: string;
  /** left inner = binary on disk */  li: string;
  /** right outer = filetree entry */ ro: string;
  /** right inner = chunk availability */ ri: string;
}

/** Ring colours — match the WPF reference; chunk state is collapsed to hydrated / not-hydrated. */
const PRESENT  = '#27272a'; // pointer / filetree present
const HYDRATED = '#2563eb'; // binary on disk, or chunk hydrated (downloadable now)
const NOTHYDR  = '#9cc4f5'; // chunk in archive tier (needs rehydration) — also covers rehydrating
const EMPTY    = '#e4e7ec'; // absent

/**
 * The Arius state ring: one disc per file split left = local disk / right = repository, each half an
 * outer ring + inner disc. Ports Arius.Explorer's StateCircle. Takes the RepositoryEntryState flags
 * (preferred) or four explicit colours; renders the exact handoff SVG at the requested pixel size.
 */
@Component({
  selector: 'arius-state-ring',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  template: `
    <svg viewBox="0 0 24 24" [attr.width]="size()" [attr.height]="size()"
         role="img" [attr.aria-label]="label()" style="display:block">
      <title>{{ label() }}</title>
      <path d="M12,12 L1,12 A11,11 0 0,1 12,1 Z M12,12 L1,12 A11,11 0 0,0 12,23 Z"        [attr.fill]="c().lo"/>
      <path d="M12,12 L23,12 A11,11 0 0,0 12,1 Z M12,12 L23,12 A11,11 0 0,1 12,23 Z"       [attr.fill]="c().ro"/>
      <path d="M12,12 L4.6,12 A7.4,7.4 0 0,1 12,4.6 Z M12,12 L4.6,12 A7.4,7.4 0 0,0 12,19.4 Z"  [attr.fill]="c().li"/>
      <path d="M12,12 L19.4,12 A7.4,7.4 0 0,0 12,4.6 Z M12,12 L19.4,12 A7.4,7.4 0 0,1 12,19.4 Z" [attr.fill]="c().ri"/>
      <line x1="12" y1="0.6" x2="12" y2="23.4" stroke="#fff" stroke-width="1.6"/>
      <circle cx="12" cy="12" r="7.4" fill="none" stroke="#fff" stroke-width="1.4"/>
    </svg>
  `,
})
export class StateRingComponent {
  /** Rendered pixel size: 19 in rows, 15 in the legend button, 76 in the legend diagram. */
  readonly size = input<number>(19);
  /** RepositoryEntryState flags from the API (preferred data path). */
  readonly state = input<number | null>(null);
  /** Explicit colour override (legend diagram / search results that already carry colours). */
  readonly colors = input<RingColors | null>(null);

  readonly c = computed<RingColors>(() => this.colors() ?? this.flagsToColors(this.state() ?? 0));
  readonly label = computed(() => this.tooltip(this.state() ?? 0));

  private flagsToColors(s: number): RingColors {
    return {
      lo: hasFlag(s, S.LocalPointer) ? PRESENT : EMPTY,
      li: hasFlag(s, S.LocalBinary) ? HYDRATED : EMPTY,
      ro: hasFlag(s, S.Repository) ? PRESENT : EMPTY,
      ri: hasFlag(s, S.RepositoryHydrated)
        ? HYDRATED
        : hasFlag(s, S.RepositoryArchived) || hasFlag(s, S.RepositoryRehydrating)
          ? NOTHYDR
          : EMPTY,
    };
  }

  private tooltip(s: number): string {
    if (s === 0) return 'Unknown';
    const inRepo = hasFlag(s, S.Repository);
    const onDisk = hasFlag(s, S.LocalBinary);
    if (hasFlag(s, S.RepositoryRehydrating)) return 'Rehydrating';
    if (inRepo && hasFlag(s, S.RepositoryArchived)) return 'Archive tier — rehydration required';
    if (inRepo && onDisk && hasFlag(s, S.RepositoryHydrated)) return 'In sync';
    if (inRepo && hasFlag(s, S.LocalPointer) && !onDisk) return 'Pointer only — chunk available';
    if (inRepo) return 'In repository';
    if (onDisk) return 'Not archived';
    return 'Mixed';
  }
}
