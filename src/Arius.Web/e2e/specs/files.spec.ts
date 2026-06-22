import { test, expect } from '../support/fixtures';
import { revealFiles } from '../support/files';

test.describe('files tab', () => {
  test.beforeEach(async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);
  });

  test('snapshot bar shows the latest snapshot + live working state + scrubber', async ({ page }) => {
    await expect(page.getByTestId('snapshot-picker')).toContainText('LATEST');
    await expect(page.getByText('Live working state')).toBeVisible();
    await expect(page.getByTestId('scrubber-dot').first()).toBeVisible();
  });

  test('lists folders in the tree and files with state rings', async ({ page, repo }) => {
    await expect(page.getByTestId('tree-node').first()).toBeVisible({ timeout: 40_000 });

    const files = await revealFiles(page, repo.repoId);
    // Every file row renders one state-ring SVG. Poll both counts together so the assertion can't race
    // the list still settling (sampling rowCount, then the count growing before the SVG check).
    await expect.poll(async () => {
      const rows = await files.count();
      const rings = await files.locator('arius-state-ring svg').count();
      return rows > 0 && rows === rings;
    }, { timeout: 20_000 }).toBe(true);
  });

  test('filter narrows the file list', async ({ page, repo }) => {
    await revealFiles(page, repo.repoId);
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
