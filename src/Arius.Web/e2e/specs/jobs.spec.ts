import { test, expect } from '../support/fixtures';

test('jobs screen renders the runs table + live console', async ({ page }) => {
  await page.goto('/jobs');
  await expect(page.getByRole('heading', { name: 'Jobs' })).toBeVisible();
  await expect(page.getByText('running')).toBeVisible();   // the "N running" pill
  await expect(page.getByTestId('live-console')).toBeVisible();

  // any job rows present (e.g. from a prior restore) carry a status pill
  if (await page.getByTestId('job-row').count() > 0) {
    await expect(page.getByTestId('job-status').first()).toBeVisible();
  }
});
