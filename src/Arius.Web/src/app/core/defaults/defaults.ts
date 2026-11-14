/**
 * The four-level defaults hierarchy as seen by the UI: Factory -> Global settings -> Repository properties -> Run.
 * Pure logic mirroring the server's resolution (Arius.Api); wire JSON is camelCase.
 */

export type Tier = 'hot' | 'cool' | 'cold' | 'archive';
export type OnDisk = 'keep' | 'keep-pointers' | 'replace';

export interface DefaultValues {
  tier: Tier;
  onDisk: OnDisk;
  fastHash: boolean;
  overwrite: boolean;
  noPointers: boolean;
}

export type OptionKey = keyof DefaultValues;

/** Values set at one level; absent or null means inherit. */
export type SetValues = { [K in OptionKey]?: DefaultValues[K] | null };

export type Source = 'factory' | 'settings' | 'repository';

export type ResolvedDefaults = { [K in OptionKey]: { value: DefaultValues[K]; source: Source } };

export type Level = 'settings' | 'repository' | 'run';

export interface Pill {
  text: string;
  canReset: boolean;
}

export const ARCHIVE_OPTIONS: OptionKey[] = ['tier', 'onDisk', 'fastHash'];
export const RESTORE_OPTIONS: OptionKey[] = ['overwrite', 'noPointers'];

export const INHERITED_FROM: Record<Source, string> = {
  factory: 'Inherited from factory defaults',
  settings: 'Inherited from global settings',
  repository: 'Inherited from repository properties',
};

export const SET_AT: Record<Level, string> = {
  settings: 'Set in global settings',
  repository: 'Set for this repository',
  run: 'Changed for this run',
};

export function fromFactory(factory: DefaultValues): ResolvedDefaults {
  const resolved = {} as Record<OptionKey, { value: unknown; source: Source }>;
  for (const key of Object.keys(factory) as OptionKey[]) resolved[key] = { value: factory[key], source: 'factory' };
  return resolved as ResolvedDefaults;
}

/** Layers `set` over `inherited`: a set value wins and is tagged with `source`; null/absent keeps the inherited entry. */
export function resolve(set: SetValues, inherited: ResolvedDefaults, source: Source): ResolvedDefaults {
  const resolved = { ...inherited } as Record<OptionKey, { value: unknown; source: Source }>;
  for (const key of Object.keys(inherited) as OptionKey[]) {
    const value = set[key];
    if (value != null) resolved[key] = { value, source };
  }
  return resolved as ResolvedDefaults;
}

export function valuesOf(r: ResolvedDefaults): DefaultValues {
  const values = {} as Record<OptionKey, unknown>;
  for (const key of Object.keys(r) as OptionKey[]) values[key] = r[key].value;
  return values as unknown as DefaultValues;
}

export function levelPill(level: 'settings' | 'repository', key: OptionKey, set: SetValues, inherited: ResolvedDefaults): Pill {
  return set[key] != null
    ? { text: SET_AT[level], canReset: true }
    : { text: INHERITED_FROM[inherited[key].source], canReset: false };
}

export function runPill(key: OptionKey, run: DefaultValues, resolved: ResolvedDefaults): Pill {
  return run[key] === resolved[key].value
    ? { text: INHERITED_FROM[resolved[key].source], canReset: false }
    : { text: SET_AT.run, canReset: true };
}

/** Any explicit pick is set at the level, even one equal to the inherited value; only `without` unsets it. */
export function withPick(set: SetValues, key: OptionKey, value: DefaultValues[OptionKey]): SetValues {
  return { ...set, [key]: value };
}

export function without(set: SetValues, key: OptionKey): SetValues {
  const { [key]: _removed, ...rest } = set;
  return rest;
}

export function sameSet(a: SetValues, b: SetValues): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)] as OptionKey[]);
  for (const key of keys) if ((a[key] ?? null) !== (b[key] ?? null)) return false;
  return true;
}

export function archiveFlags(onDisk: OnDisk): { removeLocal: boolean; writePointers: boolean } {
  return { removeLocal: onDisk === 'replace', writePointers: onDisk !== 'keep' };
}

/** Warning shown beside a destructive pick (onDisk = replace), scoped to who it reaches. */
export function replaceNote(level: Level, inheritedBy: string[] = []): string {
  if (level === 'repository') return 'Scheduled runs of this repository will do this unattended.';
  if (level === 'run') return '';
  if (inheritedBy.length === 0) return 'No repository inherits it right now.';
  const noun = inheritedBy.length === 1 ? 'repository' : 'repositories';
  return `Reaches ${inheritedBy.length} ${noun} that inherit${inheritedBy.length === 1 ? 's' : ''} it (${inheritedBy.join(', ')}), scheduled runs included.`;
}
