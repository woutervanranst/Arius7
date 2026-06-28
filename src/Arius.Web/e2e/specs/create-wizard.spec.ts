import { test, expect } from '../support/fixtures';

test('create wizard: container step requires a container name + gates on passphrase match', async ({ page }) => {
  await page.goto('/repos/create');

  await page.getByTestId('account-radio').first().click();
  await page.getByTestId('btn-continue').click();

  // step 2 — the container name is required user input; the friendly alias is optional.
  await expect(page.getByTestId('btn-create')).toBeDisabled();
  await page.getByTestId('create-container').fill('arius-my-new-repo');

  // Still disabled until passphrase == confirm
  await expect(page.getByTestId('btn-create')).toBeDisabled();
  await page.getByTestId('passphrase').fill('correct horse');
  await page.getByTestId('passphrase-confirm').fill('mismatch');
  await expect(page.getByTestId('btn-create')).toBeDisabled();
  await page.getByTestId('passphrase-confirm').fill('correct horse');
  await expect(page.getByTestId('btn-create')).toBeEnabled();

  // we do not submit (would create a new container)
});
