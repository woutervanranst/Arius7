import { test, expect } from '../support/fixtures';

test('statistics tab shows the four KPI cards with real figures', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/statistics`);
  await expect(page.getByTestId('kpi-card')).toHaveCount(4, { timeout: 30_000 });

  const filesCard = page.getByTestId('kpi-card').filter({ hasText: 'Files' }).first();
  await expect(filesCard).toContainText(/\d/);          // a real number, not just a dash
  await expect(filesCard).not.toContainText('—');

  await expect(page.getByText('Unique chunks')).toBeVisible();
});
