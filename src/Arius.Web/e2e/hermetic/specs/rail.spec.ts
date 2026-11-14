import { test, expect } from '../support/fixtures';

test('the rail has no Repos item and keeps Overview current inside a repository', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'rail' });

  await page.goto(`/repos/${repoId}`);
  await expect(page.getByTestId('repo-title')).toHaveText('rail');

  await expect(page.getByRole('navigation').getByRole('link')).toHaveText(['Overview', 'Jobs', 'Settings']);
  await expect(page.getByTestId('rail-overview')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('rail-jobs')).not.toHaveAttribute('aria-current', 'page');
});
