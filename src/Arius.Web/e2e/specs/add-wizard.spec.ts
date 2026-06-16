import { test, expect } from '../support/fixtures';

test('add-existing wizard discovers the account\'s real containers', async ({ page }) => {
  test.setTimeout(60_000);
  await page.goto('/repos/add');

  await page.getByTestId('account-radio').first().click();
  await page.getByTestId('btn-discover').click();

  // step 2 lists the live containers; we do not finalize (would create a duplicate repo)
  await expect(page.getByTestId('container-radio').first()).toBeVisible({ timeout: 40_000 });
});
