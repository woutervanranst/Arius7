import { test, expect } from '../support/fixtures';

test.describe('files tab', () => {
  test.beforeEach(async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);
  });

  test('snapshot bar shows the latest snapshot + live working state + scrubber', async ({ page }) => {
    await expect(page.getByTestId('snapshot-picker')).toContainText('LATEST');
    await expect(page.getByText('Live working state')).toBeVisible();
    await expect(page.getByTestId('scrubber-dot').first()).toBeVisible();
  });

  test('lists folders in the tree and files with state rings', async ({ page }) => {
    await expect(page.getByTestId('tree-node').first()).toBeVisible({ timeout: 40_000 });

    const files = page.getByTestId('file-row');
    await expect(files.first()).toBeVisible({ timeout: 40_000 });
    // every file row renders the state-ring SVG
    const rowCount = await files.count();
    await expect(files.locator('arius-state-ring svg')).toHaveCount(rowCount);
  });

  test('filter narrows the file list', async ({ page }) => {
    await expect(page.getByTestId('file-row').first()).toBeVisible({ timeout: 40_000 });
    await page.getByTestId('file-filter').fill('zzz-no-such-file-zzz');
    await expect(page.getByTestId('file-row')).toHaveCount(0, { timeout: 20_000 });
  });

  test('state legend popover opens with the ring diagram', async ({ page }) => {
    await page.getByTestId('legend-button').click();
    const popover = page.getByTestId('legend-popover');
    await expect(popover).toBeVisible();
    await expect(popover.locator('arius-state-ring svg')).toBeVisible();
    await expect(popover).toContainText('local disk');
  });
});
