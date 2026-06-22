import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Opens a repo's Files tab and drills down the folder tree until file rows appear, returning the
 * file-row locator with the containing folder selected. Genuinely data-agnostic: works whether files
 * sit at the repository root (the CI-seeded fixture) or nested inside folders (a real archive such as
 * a media library, where the root holds only show folders). Descends the left-most branch, waiting
 * for each folder's listing to settle before deciding whether to go deeper.
 */
export async function revealFiles(page: Page, repoId: number): Promise<Locator> {
  await page.goto(`/repos/${repoId}/files`);
  const rows = page.getByTestId('file-row');
  const folders = page.getByTestId('tree-node');
  const emptyNotice = page.getByText('No files in this folder.');

  await expect(folders.first()).toBeVisible({ timeout: 40_000 });

  for (let depth = 0; depth < 12; depth++) {
    // Wait for the current folder's listing to finish loading (files shown, or the explicit empty notice).
    await expect
      .poll(async () => (await rows.count()) > 0 || (await emptyNotice.isVisible()), { timeout: 20_000 })
      .toBeTruthy();

    if (await rows.count() > 0) {
      await expect(rows.first()).toBeVisible();
      return rows;
    }

    // No files here — descend into the next folder down the left-most branch (its first child sits
    // right after it in the pre-order tree, i.e. at index `depth`).
    if ((await folders.count()) <= depth) break;
    await folders.nth(depth).click();
  }

  throw new Error(`No files found in any folder of repo ${repoId}`);
}
