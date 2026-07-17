import { test, expect } from '../support/fixtures';

test('a rehydrating reattach shows the auto-resume toggle (#14) and hydrated-by ETA (#13)', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'rehy' });
  await control.scenario(repoId, 'rehydratingRestoreStaysPending');

  // Start a restore → parks at awaiting-cost.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-restore').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('job-pill')).toBeVisible();

  let restoreId: string | undefined;
  await expect.poll(async () => {
    const jobs = await (await request.get(`/api/jobs?repositoryId=${repoId}`)).json();
    const r = jobs.find((j: { kind: string }) => j.kind === 'restore');
    restoreId = r?.id;
    return r?.status;
  }).toBe('awaiting-cost');

  // Reattach + approve the cost; the approved run still reports chunks pending → parks at `rehydrating`.
  await page.goto(`/jobs/${restoreId}`);
  await expect(page.getByTestId('cost-modal')).toBeVisible();
  await page.getByTestId('cost-approve').click();

  await expect.poll(async () => {
    const jobs = await (await request.get(`/api/jobs?repositoryId=${repoId}`)).json();
    return jobs.find((j: { kind: string }) => j.kind === 'restore')?.status;
  }).toBe('rehydrating');

  // Reattach FRESH → attach() re-reads persisted resume state and renders the rehydration wait card.
  await page.goto(`/jobs/${restoreId}`);
  await expect(page.getByTestId('job-status')).toContainText('Rehydrating');
  const toggle = page.getByTestId('autoresume-toggle');
  await expect(toggle).toBeVisible();                                  // real toggle, seeded from resume
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText(/hydrated by/).first()).toBeVisible();   // ETA from persisted window
});
