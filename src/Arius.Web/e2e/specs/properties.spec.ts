import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

test('properties shows the repo fields and supports schedule add + delete', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/files`);
  await page.getByTestId('btn-properties').click();
  await expect(page.getByTestId('properties-drawer')).toBeVisible();

  await expect(page.getByTestId('prop-alias')).toHaveValue(repo.alias);
  await expect(page.getByTestId('prop-container')).toHaveValue(repo.container);

  const before = await page.getByTestId('schedule-row').count();
  await page.getByTestId('schedule-cron').fill('0 2 * * *');
  await page.getByTestId('schedule-add').click();
  await expect(page.getByTestId('schedule-row')).toHaveCount(before + 1, { timeout: 15_000 });

  // cleanup — delete the schedule we just added
  await page.getByTestId('schedule-delete').last().click();
  await expect(page.getByTestId('schedule-row')).toHaveCount(before, { timeout: 15_000 });
});

// Runs against a throwaway repo so rotating the passphrase doesn't affect the shared source repo's
// existing snapshots. DB-only (no archive), so it needs no @write.
test('properties: passphrase requires a matching confirmation, then saves', async ({ page, request, repo }) => {
  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer('passphrase'), alias: 'E2E Passphrase', localPath: '', defaultTier: 'cold', passphrase: 'original' },
  })).json();

  try {
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-properties').click();
    await expect(page.getByTestId('properties-drawer')).toBeVisible();
    const save = page.getByRole('button', { name: 'Save changes' });

    // A new passphrase whose confirmation differs blocks Save and shows the mismatch hint.
    await page.getByTestId('prop-passphrase').fill('new-secret');
    await page.getByTestId('prop-passphrase-confirm').fill('mismatch');
    await expect(page.getByText("Passphrases don't match.")).toBeVisible();
    await expect(save).toBeDisabled();

    // Matching the confirmation re-enables Save; saving the rotation succeeds.
    await page.getByTestId('prop-passphrase-confirm').fill('new-secret');
    await expect(save).toBeEnabled();
    await save.click();
    await expect(page.getByText('Saved.')).toBeVisible();
  } finally {
    await request.delete(`/api/repos/${created.id}`);
  }
});
