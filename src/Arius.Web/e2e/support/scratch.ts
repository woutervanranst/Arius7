/**
 * Scratch containers created by the destructive @write specs (real archive/restore targets), kept
 * separate from the read-only source repository. The prefix is what `global-setup` purges and what
 * the `repo` fixture excludes when picking the source — so all three must agree on it.
 *
 * Defaults to `<ARIUS_E2E_CONTAINER>-e2e-arius-` when a source container is configured (scopes the
 * scratch containers to the test repo, so different accounts/environments don't collide), else the
 * bare `e2e-arius-` marker. Override wholesale with ARIUS_E2E_WRITE_PREFIX.
 */
export const SCRATCH_PREFIX =
  process.env.ARIUS_E2E_WRITE_PREFIX ??
  (process.env.ARIUS_E2E_CONTAINER ? `${process.env.ARIUS_E2E_CONTAINER}-e2e-arius-` : 'e2e-arius-');

export const scratchContainer = (suffix: string) => `${SCRATCH_PREFIX}${suffix}`;
