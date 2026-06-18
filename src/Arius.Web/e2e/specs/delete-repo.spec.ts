import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Creates a throwaway repo registration and removes it through the Properties danger zone. DB-only
// (no archive), so it needs no @write; global-setup also purges any scratch repo a failed run leaves.
test('delete repository removes it from Arius and returns to the overview', async ({ page, request, repo }) => {
  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer('delete'), alias: 'E2E Delete Me', localPath: '', defaultTier: 'cold', passphrase: 'e2etest' },
  })).json();

  try {
    await page.goto(`/repos/${created.id}/properties`);
    await page.getByTestId('prop-delete').click();           // arm the inline confirm
    await page.getByTestId('prop-delete-confirm').click();   // confirm → DELETE + navigate

    await expect(page).toHaveURL(/\/overview$/);

    const repos = await (await request.get('/api/repos')).json();
    expect(repos.some((r: { id: number }) => r.id === created.id)).toBe(false);
  } finally {
    await request.delete(`/api/repos/${created.id}`);   // no-op (404) if the UI already deleted it
  }
});
