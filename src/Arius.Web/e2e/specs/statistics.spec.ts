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

  // The dedup/compression "savings" banner was removed for good.
  await expect(page.getByTestId('savings')).toHaveCount(0);
});

test('the "This snapshot" figures follow the snapshot selected in the bar', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/statistics`);
  await expect(page.getByTestId('storage-loading')).toBeHidden({ timeout: 60_000 });

  await page.getByTestId('snapshot-picker').click();
  const items = page.getByTestId('snapshot-item');
  await expect(items.first()).toBeVisible({ timeout: 30_000 });
  test.skip(await items.count() < 2, 'needs more than one snapshot to switch the snapshot scope');

  // Selecting an older snapshot must re-query the snapshot-scoped figures with that version (the
  // repository-storage figures are repo-wide and intentionally stay put).
  const versioned = page.waitForRequest(r => /\/repos\/\d+\/stats\?.*\bversion=/.test(r.url()));
  await items.last().click(); // the oldest snapshot
  await versioned;

  // The shared bar reflects the historical selection on the Statistics tab too.
  await expect(page.getByText('Historical view')).toBeVisible();
});

test('statistics are cached server-side: repeated loads serve identical figures', async ({ request, repo }) => {
  // The API memoizes the (expensive full-coverage) statistics in its own database, keyed by the latest
  // snapshot fingerprint. While the snapshot set is unchanged, every call must return the same figures.
  // First call primes the cache (may compute); the rest are cache hits.
  const url = `/api/repos/${repo.repoId}/stats?full=true`;

  const first = await request.get(url);
  expect(first.ok(), `GET ${url} failed (${first.status()})`).toBeTruthy();
  const firstBody = await first.json();

  // A cache hit must reproduce the figures exactly (not just shape) — and quickly.
  const startedAt = Date.now();
  const second = await request.get(url);
  const elapsed = Date.now() - startedAt;
  expect(second.ok()).toBeTruthy();
  const secondBody = await second.json();

  expect(secondBody).toEqual(firstBody);
  // The cached read skips the chunk-index download, so the second call returns promptly.
  expect(elapsed).toBeLessThan(15_000);

  // The snapshot-scoped variant (no full coverage) is cached independently and is likewise stable.
  const snap1 = await (await request.get(`/api/repos/${repo.repoId}/stats`)).json();
  const snap2 = await (await request.get(`/api/repos/${repo.repoId}/stats`)).json();
  expect(snap2).toEqual(snap1);
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

  // The redesigned breakdown carries an estimated monthly cost per tier plus a grand total.
  await expect(breakdown).toContainText('Est. cost/mo');
  await expect(rows.first().getByTestId('tier-cost')).toBeVisible();
  await expect(breakdown.getByTestId('total-cost')).toBeVisible();
});
