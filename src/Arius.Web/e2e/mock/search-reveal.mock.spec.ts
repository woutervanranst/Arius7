import { test, expect } from '@playwright/test';
import { installMocks, REPO } from './signalr-mock';

/**
 * Reproduces the ARI-4 scenario end-to-end in a real browser against mocked REST + SignalR:
 * type in global search, click a result for a *nested* file, and assert we land on the right
 * repo with the tree expanded to the file's folder and the file row highlighted.
 */
test.beforeEach(async ({ page }) => {
  await installMocks(page);
});

test('clicking a global-search result reveals & highlights the file', async ({ page }) => {
  // Start somewhere the top-bar search is visible (it is hidden on Overview).
  await page.goto('/jobs');

  await page.getByTestId('topbar-search').click();
  await page.getByTestId('search-input').fill('guide');

  const result = page.getByTestId('search-result').first();
  await expect(result).toBeVisible();
  await expect(result).toContainText(REPO.alias);
  await expect(result).toContainText('docs/guide.txt');

  await result.click();

  // 1) Navigated to the right repo's Files tab, carrying the file path (slash may be percent-encoded).
  await expect(page).toHaveURL(new RegExp(`/repos/${REPO.id}/files\\?path=docs(%2F|/)guide\\.txt`));

  // 2) The containing folder is expanded/selected in the tree.
  const docsNode = page.getByTestId('tree-node').filter({ hasText: 'docs' });
  await expect(docsNode).toHaveClass(/sel/);

  // 3) The file row is shown AND highlighted (the .hl class added by the fix).
  const guideRow = page.getByTestId('file-row').filter({ hasText: 'guide.txt' });
  await expect(guideRow).toBeVisible();
  await expect(guideRow).toHaveClass(/hl/);

  // The path-bar reflects the revealed folder.
  await expect(page.getByText('/mock-container/docs')).toBeVisible();
});

test('a root-level file reveals with no folder expansion needed', async ({ page }) => {
  // Navigate straight to the Files tab with a root file in the query param.
  await page.goto(`/repos/${REPO.id}/files?path=readme.txt`);

  const readmeRow = page.getByTestId('file-row').filter({ hasText: 'readme.txt' });
  await expect(readmeRow).toBeVisible();
  await expect(readmeRow).toHaveClass(/hl/);
});
