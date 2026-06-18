import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Destructive: archives four distinct files into one repo, each at a different access tier, so the
// statistics tier breakdown has more than the single tier the shared seed produces — the multi-tier
// coverage the old statistics-tab unit test held but the live suite couldn't reach. Per-run-unique
// container; the global teardown deletes scratch containers afterwards.
test('statistics tier breakdown lists every archived tier, in API order @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive multi-tier archive');
  test.setTimeout(420_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-tiers-'));
  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`tiers-${Date.now()}`), alias: 'E2E Tiers', passphrase: 'e2etest', localPath: src, defaultTier: 'cold' },
  })).json();

  // Each run adds one new file (distinct content → a new chunk) and uploads it at the chosen tier.
  const archiveAtTier = async (tier: string, file: string) => {
    fs.writeFileSync(path.join(src, file), `arius e2e ${tier} ${Date.now()}`);
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.locator(`[data-testid="tier-seg"][data-tier="${tier}"]`).click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByText('Archive complete', { exact: false })).toBeVisible({ timeout: 180_000 });
    await page.getByRole('button', { name: 'Close' }).click();
  };

  try {
    await archiveAtTier('hot', 'a.txt');
    await archiveAtTier('cool', 'b.txt');
    await archiveAtTier('cold', 'c.txt');
    await archiveAtTier('archive', 'd.txt');

    // Stats read from the local chunk-index cache; warm it by browsing Files, then wait until the
    // backend reports all four tiers.
    await page.goto(`/repos/${created.id}/files`);
    await expect(page.getByTestId('file-row').first()).toBeVisible({ timeout: 40_000 });

    let storedByTier: { tier: string }[] = [];
    await expect.poll(async () => {
      const stats = await (await request.get(`/api/repos/${created.id}/stats`)).json();
      storedByTier = stats?.storedByTier ?? [];
      return storedByTier.length;
    }, { timeout: 60_000, message: 'expected all four tiers in storedByTier' }).toBeGreaterThanOrEqual(4);

    for (const tier of ['Hot', 'Cool', 'Cold', 'Archive']) {
      expect(storedByTier.map(t => t.tier), `tier ${tier} present`).toContain(tier);
    }

    // The UI renders one row per storedByTier entry, in the same (API) order.
    await page.goto(`/repos/${created.id}/statistics`);
    const breakdown = page.getByTestId('tier-breakdown');
    await expect(breakdown).toBeVisible({ timeout: 30_000 });
    const rows = breakdown.getByTestId('tier-row');
    await expect(rows).toHaveCount(storedByTier.length);
    for (let i = 0; i < storedByTier.length; i++) {
      await expect(rows.nth(i)).toContainText(storedByTier[i].tier);
      await expect(rows.nth(i)).toContainText('chunks');
    }
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
