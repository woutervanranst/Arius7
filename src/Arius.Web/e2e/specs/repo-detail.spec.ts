import { test, expect } from '../support/fixtures';

// Repo shell: header actions, the tab bar, and the snapshot bar that was promoted above the tabs.
test.describe('repo detail shell', () => {
  test('header actions read Restore · Archive · Properties, left to right', async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);

    const left = async (testId: string) => {
      const box = await page.getByTestId(testId).boundingBox();
      if (!box) throw new Error(`${testId} has no bounding box`);
      return box.x;
    };

    await expect(page.getByTestId('btn-restore')).toBeVisible();
    await expect(page.getByTestId('btn-archive')).toBeVisible();
    await expect(page.getByTestId('btn-properties')).toBeVisible();
    expect(await left('btn-restore')).toBeLessThan(await left('btn-archive'));
    expect(await left('btn-archive')).toBeLessThan(await left('btn-properties'));
  });

  test('Properties is a drawer, not a tab; tabs are just Files and Statistics', async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);

    await expect(page.getByTestId('tab-files')).toBeVisible();
    await expect(page.getByTestId('tab-statistics')).toBeVisible();
    await expect(page.getByTestId('tab-properties')).toHaveCount(0);

    // The cogwheel opens Properties as a side panel (same pattern as Restore/Archive).
    await page.getByTestId('btn-properties').click();
    await expect(page.getByTestId('properties-drawer')).toBeVisible();
  });

  test('the snapshot bar sits above the tabs and persists across Files and Statistics', async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);
    await expect(page.getByTestId('snapshot-picker')).toBeVisible();

    // The bar lives in the repo shell, so switching tabs keeps it on screen.
    await page.getByTestId('tab-statistics').click();
    await expect(page).toHaveURL(/\/statistics$/);
    await expect(page.getByTestId('snapshot-picker')).toBeVisible();
  });
});
