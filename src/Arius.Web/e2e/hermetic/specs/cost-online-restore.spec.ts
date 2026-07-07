import { test, expect } from '../support/fixtures';

test('the cost modal drops the priority choice when nothing needs rehydration (#3)', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'online' });
  await control.scenario(repoId, 'onlineRestore');

  // Start a whole-repo restore → it parks at awaiting-cost with a cost estimate that needs no rehydration.
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

  await page.goto(`/jobs/${restoreId}`);
  await expect(page.getByTestId('cost-modal')).toBeVisible();

  // #3: nothing to rehydrate → no Standard/High choice, and the confirm button just says "Restore".
  await expect(page.getByTestId('prio-standard')).toHaveCount(0);
  await expect(page.getByTestId('prio-high')).toHaveCount(0);
  await expect(page.getByTestId('cost-approve')).toHaveText('Restore');
  await expect(page.getByTestId('cost-modal')).toContainText('no rehydration needed');

  // Approving restores with no priority decision required.
  await page.getByTestId('cost-approve').click();
  await expect.poll(async () => {
    const jobs = await (await request.get(`/api/jobs?repositoryId=${repoId}`)).json();
    return jobs.find((j: { kind: string }) => j.kind === 'restore')?.status;
  }).toBe('completed');
});

test('the awaiting-cost banner clears after approving and does not linger', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'online-gated' });
  // Gated: the run pauses after approval, so no live "running" transition reaches the client — the exact
  // condition under which the "paused for cost approval" banner used to linger after the modal closed.
  await control.scenario(repoId, 'onlineRestore', true);

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

  await page.goto(`/jobs/${restoreId}`);
  await expect(page.getByTestId('cost-modal')).toBeVisible();
  await page.getByTestId('cost-approve').click();

  // Modal closes AND the paused-for-cost banner must not appear, even though the gated run leaves the client's
  // status at 'awaiting-cost' (no live transition pushed).
  await expect(page.getByTestId('cost-modal')).toHaveCount(0);
  await expect(page.getByText('paused for cost approval')).toHaveCount(0);

  await control.release(repoId);   // let the gated run finish so teardown is clean
});
