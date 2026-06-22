import { test, expect } from '../support/fixtures';

test('statistics tab shows the five KPI cards with real figures', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/statistics`);

  // The two snapshot cards render immediately; the three repository-storage cards lazy-load behind a
  // spinner (full chunk-index coverage). Wait for that to finish, then all five carry real figures.
  await expect(page.getByTestId('storage-loading')).toBeHidden({ timeout: 60_000 });
  const cards = page.getByTestId('kpi-card');
  await expect(cards).toHaveCount(5, { timeout: 30_000 });

  // every card shows a real figure, not just the placeholder dash
  for (let i = 0; i < 5; i++) {
    await expect(cards.nth(i)).toContainText(/\d/);
    await expect(cards.nth(i)).not.toContainText('—');
  }

  await expect(page.getByText('Original size')).toBeVisible();
  await expect(page.getByText('Deduplicated size')).toBeVisible();
  await expect(page.getByText('Unique chunks')).toBeVisible();
});

test('statistics tab shows the stored-size-by-tier breakdown', async ({ page, repo }) => {
  // The repository-storage figures (and the tier breakdown) load the full chunk index server-side, so
  // they no longer depend on having browsed the Files tab first.
  await page.goto(`/repos/${repo.repoId}/statistics`);
  await expect(page.getByTestId('storage-loading')).toBeHidden({ timeout: 60_000 });

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
