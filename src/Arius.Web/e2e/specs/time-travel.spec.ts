import { test, expect } from '../support/fixtures';

test('snapshot picker lists snapshots; selecting a non-latest one shows the historical view', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/files`);
  await page.getByTestId('snapshot-picker').click();

  const items = page.getByTestId('snapshot-item');
  await expect(items.first()).toBeVisible({ timeout: 30_000 });

  if (await items.count() > 1) {
    await items.last().click(); // newest-first list → last row is the oldest snapshot (v1), so the historical view shows
    await expect(page.getByText('Historical view')).toBeVisible();
  }
});

test('snapshot dropdown is newest-first (LATEST on top) and shows no file count', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/files`);
  await page.getByTestId('snapshot-picker').click();

  const items = page.getByTestId('snapshot-item');
  await expect(items.first()).toBeVisible({ timeout: 30_000 });
  const count = await items.count();

  // Newest-first: the top row carries LATEST (and is the default selection).
  await expect(items.first()).toContainText('LATEST');

  if (count > 1) {
    // Older snapshots follow below; only the top (newest) row is LATEST, and the bottom is v1.
    await expect(items.nth(1)).not.toContainText('LATEST');
    await expect(items.last()).toContainText('v1');
  }

  // The per-row file count was removed from the dropdown.
  for (let i = 0; i < count; i++) {
    await expect(items.nth(i)).not.toContainText('files');
  }
});
