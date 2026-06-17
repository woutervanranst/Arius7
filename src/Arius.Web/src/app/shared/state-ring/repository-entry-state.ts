/**
 * Mirrors Arius.Core's [Flags] RepositoryEntryState. The API serializes the raw int (`state`) plus a
 * decoded object; the State Ring reads the int and decodes here.
 */
export const RepositoryEntryState = {
  None: 0,
  LocalPointer: 1 << 0,
  LocalBinary: 1 << 1,
  LocalDirectory: 1 << 2,
  Repository: 1 << 3,
  RepositoryHydrated: 1 << 4,
  RepositoryArchived: 1 << 5,
  RepositoryRehydrating: 1 << 6,
} as const;

export function hasFlag(state: number, flag: number): boolean {
  return (state & flag) === flag;
}
