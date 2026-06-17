import { test, expect } from '../support/fixtures';

test.describe('shell', () => {
  test('icon rail navigates between top-level screens', async ({ page }) => {
    await page.goto('/overview');
    await expect(page.getByTestId('breadcrumb-current')).toHaveText('Overview');

    await page.getByTestId('rail-jobs').click();
    await expect(page).toHaveURL(/\/jobs/);
    await expect(page.getByTestId('breadcrumb-current')).toHaveText('Jobs');

    await page.getByTestId('rail-repos').click();
    await expect(page).toHaveURL(/\/repos/);

    await page.getByTestId('rail-settings').click();
    await expect(page).toHaveURL(/\/settings/);

    await page.getByTestId('rail-overview').click();
    await expect(page).toHaveURL(/\/overview/);
  });

  test('global search is hidden on Overview, visible elsewhere; opens via box and closes on Esc', async ({ page }) => {
    await page.goto('/overview');
    await expect(page.getByTestId('topbar-search')).toBeHidden();

    await page.goto('/jobs');
    await expect(page.getByTestId('topbar-search')).toBeVisible();
    await page.getByTestId('topbar-search').click();
    await expect(page.getByTestId('search-input')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('search-input')).toBeHidden();
  });
});
