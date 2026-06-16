import { test, expect } from '../support/fixtures';

test('create wizard: account step → new-container form auto-fills + gates on passphrase match', async ({ page }) => {
  await page.goto('/repos/create');

  await page.getByTestId('account-radio').first().click();
  await page.getByTestId('btn-continue').click();

  // step 2 — alias auto-generates a mono container name
  await page.getByTestId('create-alias').fill('My New Repo');
  await expect(page.getByTestId('create-container')).toHaveValue(/^arius-my-new-repo-[0-9a-f]{4}$/);

  // Create is disabled until passphrase == confirm
  await expect(page.getByTestId('btn-create')).toBeDisabled();
  await page.getByTestId('passphrase').fill('correct horse');
  await page.getByTestId('passphrase-confirm').fill('mismatch');
  await expect(page.getByTestId('btn-create')).toBeDisabled();
  await page.getByTestId('passphrase-confirm').fill('correct horse');
  await expect(page.getByTestId('btn-create')).toBeEnabled();

  // we do not submit (would create a new container)
});
