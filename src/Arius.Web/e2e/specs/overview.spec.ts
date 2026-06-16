import { test, expect } from '../support/fixtures';

test('overview shows KPI cards and the repositories table; a row opens the repo', async ({ page, repo }) => {
  await page.goto('/overview');
  await expect(page.getByTestId('kpi-card')).toHaveCount(4);

  const row = page.getByTestId('repo-row').filter({ hasText: repo.alias }).first();
  await expect(row).toContainText(repo.alias);
  await expect(row).toContainText(repo.container);

  await row.click();
  await expect(page).toHaveURL(new RegExp(`/repos/${repo.repoId}/files`));
});
