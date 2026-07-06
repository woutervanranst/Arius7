import { test, expect } from '../support/fixtures';

test('the cost modal renders on a fresh reattach of an awaiting-cost restore (#2)', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'cost' });
  await control.scenario(repoId, 'rehydratingRestore');

  // Start a whole-repo restore from the repo page → drawer dismisses → pill appears; it parks at awaiting-cost.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-restore').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('job-pill')).toBeVisible();

  // Wait for the server to park it, capturing the job id.
  let restoreId: string | undefined;
  await expect.poll(async () => {
    const jobs = await (await request.get(`/api/jobs?repositoryId=${repoId}`)).json();
    const r = jobs.find((j: { kind: string }) => j.kind === 'restore');
    restoreId = r?.id;
    return r?.status;
  }).toBe('awaiting-cost');

  // Reattach FRESH to the detail page (new navigation) → the cost modal renders from the reattach state.
  await page.goto(`/jobs/${restoreId}`);
  await expect(page.getByTestId('cost-modal')).toBeVisible();          // #2: cost flows on reattach
  await expect(page.getByTestId('prio-standard')).toBeVisible();
  await expect(page.getByTestId('prio-high')).toBeVisible();

  // #2: the modal must default to Standard (the cheaper option), not High.
  await expect(page.getByTestId('prio-standard')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('prio-high')).toHaveAttribute('aria-pressed', 'false');

  // …and it's surfaced in the jobs list under "Needs your attention".
  await page.goto('/jobs');
  await expect(page.getByTestId('jobs-needs-attention').getByTestId('job-review-cost')).toBeVisible();
});
