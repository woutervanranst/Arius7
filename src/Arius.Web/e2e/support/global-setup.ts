import { request } from '@playwright/test';
import { SCRATCH_PREFIX } from './scratch';

const API = 'http://localhost:5080';

/**
 * Ensures a repository exists before the suite runs. If the app DB already has one (e.g. a reused
 * dev DB), it's used as-is. Otherwise, if ARIUS_E2E_* env vars are present, an account + repository
 * are seeded. With neither, the run fails fast rather than passing against no data.
 */
export default async function globalSetup() {
  const ctx = await request.newContext();

  // Defensive health wait (webServer normally already ensured this).
  for (let i = 0; i < 60; i++) {
    try { if ((await ctx.get(`${API}/api/health`)).ok()) break; } catch { /* not up yet */ }
    await new Promise(r => setTimeout(r, 1000));
  }

  // Purge leftover scratch repos from previous (possibly crashed) @write runs.
  const existing = await (await ctx.get(`${API}/api/repos`)).json();
  if (Array.isArray(existing)) {
    for (const r of existing.filter((x: { container: string }) => x.container.startsWith(SCRATCH_PREFIX)))
      await ctx.delete(`${API}/api/repos/${r.id}`);
  }

  const repos = (await (await ctx.get(`${API}/api/repos`)).json()) as unknown[];
  if (Array.isArray(repos) && repos.length > 0) {
    await ctx.dispose();
    return;
  }

  const account = process.env.ARIUS_E2E_ACCOUNT;
  const container = process.env.ARIUS_E2E_CONTAINER;
  if (!account || !container) {
    await ctx.dispose();
    throw new Error(
      'No repository configured. Set ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY / ARIUS_E2E_CONTAINER / ' +
      'ARIUS_E2E_PASSPHRASE, or pre-seed a repository in the app DB before running the e2e suite.',
    );
  }

  const created = await ctx.post(`${API}/api/accounts`, {
    data: { name: account, accountKey: process.env.ARIUS_E2E_KEY ?? null },
  });
  const accountId = (await created.json()).id;

  await ctx.post(`${API}/api/repos`, {
    data: {
      accountId,
      container,
      alias: process.env.ARIUS_E2E_ALIAS ?? container,
      passphrase: process.env.ARIUS_E2E_PASSPHRASE ?? null,
      localPath: '',
      defaultTier: process.env.ARIUS_E2E_TIER ?? 'cold',
    },
  });

  await ctx.dispose();
}
