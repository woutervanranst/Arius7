import { test, expect } from '../support/fixtures';

test('the rail keeps Overview current inside a repository', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'rail' });

  await page.goto(`/repos/${repoId}`);
  await expect(page.getByTestId('repo-title')).toHaveText('rail');

  await expect(page.getByTestId('rail-overview')).toHaveAttribute('aria-current', 'page');
});
