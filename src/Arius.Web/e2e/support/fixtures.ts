import { test as base, expect } from '@playwright/test';

export interface RepoInfo {
  repoId: number;
  accountId: number;
  alias: string;
  container: string;
  localPath: string | null;
  defaultTier: string;
}

/**
 * Provides the first configured repository (id/alias/container) so specs don't hardcode an id, plus
 * a `patchRepo` helper for specs that temporarily change `localPath` (archive) and restore it after.
 */
export const test = base.extend<{ repo: RepoInfo; patchRepo: (id: number, body: Record<string, unknown>) => Promise<void> }>({
  repo: async ({ request }, use) => {
    const repos = await (await request.get('/api/repos')).json();
    if (!Array.isArray(repos) || repos.length === 0) throw new Error('No repository available (global setup did not seed one).');
    // Prefer the configured container; otherwise the first non-e2e-scratch repo; never a leftover e2e-* repo.
    const wanted = process.env.ARIUS_E2E_CONTAINER;
    const r =
      repos.find((x: { container: string }) => x.container === wanted) ??
      repos.find((x: { container: string }) => !x.container.startsWith('e2e-arius-')) ??
      repos[0];
    await use({ repoId: r.id, accountId: r.accountId, alias: r.alias, container: r.container, localPath: r.localPath, defaultTier: r.defaultTier });
  },
  patchRepo: async ({ request }, use) => {
    await use(async (id, body) => { await request.patch(`/api/repos/${id}`, { data: body }); });
  },
});

export { expect };
