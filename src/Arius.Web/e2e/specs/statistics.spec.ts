import { test, expect } from '../support/fixtures';

test('statistics tab shows the four KPI cards with real figures', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/statistics`);
  const cards = page.getByTestId('kpi-card');
  await expect(cards).toHaveCount(4, { timeout: 30_000 });

  // every card shows a real figure, not just the placeholder dash
  for (let i = 0; i < 4; i++) {
    await expect(cards.nth(i)).toContainText(/\d/);
    await expect(cards.nth(i)).not.toContainText('—');
  }

  await expect(page.getByText('Unique chunks')).toBeVisible();
});

test('statistics tab shows the stored-size-by-tier breakdown', async ({ page, repo }) => {
  // Stats read straight from the local chunk-index cache (no blob reads), so the tier breakdown
  // only appears once the cache holds coverage. Browse the Files tab first to warm it (LookupAsync),
  // mirroring real usage where Files is the repo's default tab.
  await page.goto(`/repos/${repo.repoId}/files`);
  await expect(page.getByTestId('tree-node').first()).toBeVisible({ timeout: 40_000 });

  await page.goto(`/repos/${repo.repoId}/statistics`);

  const breakdown = page.getByTestId('tier-breakdown');
  await expect(breakdown).toBeVisible({ timeout: 30_000 });
  await expect(breakdown).toContainText('Stored size by tier');

  // The configured repo is single-tier (the suite seeds 'hot'); a real repo may have more — so
  // assert at least one row, and that each names a known Azure access tier.
  const rows = breakdown.getByTestId('tier-row');
  await expect(rows.first()).toBeVisible();
  expect(await rows.count()).toBeGreaterThan(0);
  await expect(rows.first()).toContainText(/Hot|Cool|Cold|Archive/);
  await expect(rows.first()).toContainText(/chunks/);
});
