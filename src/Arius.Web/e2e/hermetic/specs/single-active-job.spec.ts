import { test, expect } from '../support/fixtures';

test('a second job on a busy repo is rejected in the UI (#1, by design)', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'busy' });
  await control.scenario(repoId, 'rehydratingRestore');

  // Park a restore at awaiting-cost — the repo is now busy (HasActiveJob true).
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-restore').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('job-pill')).toBeVisible();
  await expect.poll(async () => {
    const jobs = await (await request.get(`/api/jobs?repositoryId=${repoId}`)).json();
    return jobs.find((j: { kind: string }) => j.kind === 'restore')?.status;
  }).toBe('awaiting-cost');

  // Attempt to start an archive on the SAME repo → the hub rejects; the drawer surfaces the error inline.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-archive').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('start-error')).toBeVisible();
  await expect(page.getByTestId('start-error')).toContainText(/already running/i);
  await expect(page.getByTestId('drawer')).toBeVisible();              // drawer stays open (not silently stuck)
});
