import { test, expect } from '../support/fixtures';

// The default suite covers the restore drawer UI non-destructively. A real streaming restore (with a
// file count) runs in archive.spec under ARIUS_E2E_WRITE against a small dedicated repo, because the
// shared repo can be large and whole-repo restore would be slow.
test('restore drawer opens from the repo header with a target summary', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/files`);

  await page.getByTestId('btn-restore').click();
  await expect(page.getByTestId('drawer')).toBeVisible();
  await expect(page.getByTestId('drawer-title')).toContainText('Restore');
  await expect(page.getByText('Whole repository')).toBeVisible();
  await expect(page.getByTestId('drawer-start')).toBeVisible();

  await page.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByTestId('drawer')).toBeHidden();
});
