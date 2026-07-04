import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// The /jobs/:id detail page drives both archive and restore: header + status chip, the layered
// progress bar, KPI tiles, stage list, footer/warnings and (for a parked restore) the cost modal.

test('job detail page renders for an existing job', async ({ page, request }) => {
  const jobs = await (await request.get('/api/jobs')).json();
  test.skip(!Array.isArray(jobs) || jobs.length === 0, 'no jobs in history to open');

  await page.goto(`/jobs/${jobs[0].id}`);
  await expect(page.getByTestId('job-detail')).toBeVisible();
  await expect(page.getByTestId('layered-bar')).toBeVisible();
  await expect(page.getByTestId('job-status')).toBeVisible();
});

// Destructive + slow: archives a tiny folder to the Hot tier in a dedicated container, then restores
// it to an empty destination. An online restore surfaces the estimated-cost modal on the job page;
// approving it lets the download proceed (no rehydration, so no priority choice, no real cost).
test('restore cost modal + approve flow lives on the job page @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive restore flow');
  test.setTimeout(300_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-jobdetail-'));
  fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`jobdetail-${Date.now()}`), alias: 'E2E Job Detail', passphrase: 'e2etest', localPath: src, defaultTier: 'hot' },
  })).json();

  try {
    // 1) archive to the Hot tier, then wait for it to finish (via the API — the pill/console is transient)
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });
    await expect.poll(async () => {
      const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
      return jobs.find((j: { kind: string }) => j.kind === 'archive')?.status;
    }, { timeout: 180_000 }).toBe('completed');

    // 2) clear localPath so the restore writes to a fresh temp dir (the file must actually be restored)
    await request.patch(`/api/repos/${created.id}`, { data: { localPath: '' } });

    // 3) whole-repo restore from the header → drawer dismisses → pill appears
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-restore').click();
    await page.getByTestId('drawer-start').click();

    // 4) open the restore job's detail page and assert the page + cost modal render there
    let restoreId: string | undefined;
    await expect.poll(async () => {
      const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
      restoreId = jobs.find((j: { kind: string }) => j.kind === 'restore')?.id;
      return restoreId;
    }, { timeout: 120_000 }).toBeTruthy();

    await page.goto(`/jobs/${restoreId}`);
    await expect(page.getByTestId('job-detail')).toBeVisible();
    await expect(page.getByTestId('layered-bar')).toBeVisible();
    await expect(page.getByTestId('cost-modal')).toBeVisible({ timeout: 120_000 });

    // 5) approve — the modal closes and the download proceeds
    await page.getByTestId('cost-approve').click();
    await expect(page.getByTestId('cost-modal')).toBeHidden();
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
