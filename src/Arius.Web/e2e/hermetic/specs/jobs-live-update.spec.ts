import { test, expect } from '../support/fixtures';

test('a finishing archive leaves Active live and the running chip drops (#7)', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'live' });
  await control.scenario(repoId, 'representativeArchive', /* gated */ true);

  // Start the archive from the repo page (drives the real StartArchive hub call).
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-archive').click();
  await page.getByTestId('drawer-start').click();

  // It shows up as Active on /jobs; the running chip counts it.
  await page.goto('/jobs');
  const active = page.getByTestId('jobs-active');
  await expect(active.getByTestId('job-row')).toHaveCount(1);
  await expect(page.getByText('1 running')).toBeVisible();

  // Release the gate → the archive completes → jobDone → the list re-fetches.
  await control.release(repoId);

  await expect(active.getByTestId('job-row')).toHaveCount(0);          // left Active
  await expect(active.getByText('No active jobs.')).toBeVisible();
  await expect(page.getByText('0 running')).toBeVisible();             // chip updated
  await expect(page.getByTestId('jobs-history').getByTestId('job-row')).toHaveCount(1);  // now in history
});
