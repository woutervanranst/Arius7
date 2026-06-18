import { test, expect } from '../support/fixtures';

test('snapshot picker lists snapshots; selecting a non-latest one shows the historical view', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/files`);
  await page.getByTestId('snapshot-picker').click();

  const items = page.getByTestId('snapshot-item');
  await expect(items.first()).toBeVisible({ timeout: 30_000 });

  if (await items.count() > 1) {
    await items.first().click(); // the oldest snapshot (v1) — never the latest, so the historical view shows
    await expect(page.getByText('Historical view')).toBeVisible();
  }
});
