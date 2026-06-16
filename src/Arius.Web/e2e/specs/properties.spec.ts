import { test, expect } from '../support/fixtures';

test('properties shows the repo fields and supports schedule add + delete', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/properties`);

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
