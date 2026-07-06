import { test, expect } from '../support/fixtures';

test('hermetic host serves the app and seeds a repo', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'smoke' });
  expect(repoId).toBeGreaterThan(0);
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
});
