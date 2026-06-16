import { test, expect } from '../support/fixtures';

test('snapshot picker lists snapshots; selecting a non-latest one shows the historical view', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/files`);
  await page.getByTestId('snapshot-picker').click();

  const items = page.getByTestId('snapshot-item');
  await expect(items.first()).toBeVisible({ timeout: 30_000 });

  if (await items.count() > 1) {
    await items.nth(1).click(); // an older snapshot
    await expect(page.getByText('Historical view')).toBeVisible();
  }
});
