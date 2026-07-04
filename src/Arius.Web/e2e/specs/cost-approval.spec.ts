import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Destructive + slow: archives a file to the Archive tier in a dedicated container, then restores it
// to an empty destination so the rehydration cost-approval modal actually triggers (declined — no
// real rehydration is run). The cost modal now lives on the /jobs/:id detail page (moved off the
// drawer): Start dismisses the drawer and hands the job to the pill; the detail page renders the cost
// modal from the attach-replayed CostEstimate while the restore is parked at confirm-cost.
test('restore of archive-tier data opens the cost-approval modal on the job page @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive cost-approval flow');
  test.setTimeout(300_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-cost-'));
  fs.writeFileSync(path.join(src, 'archived.bin'), Buffer.alloc(2_000_000, 7)); // 2 MB → large chunk → Archive tier

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`cost-${Date.now()}`), alias: 'E2E Cost Target', passphrase: 'e2etest', localPath: src, defaultTier: 'archive' },
  })).json();

  try {
    // 1) archive to the Archive tier, then wait for it to finish (via the API — the pill is transient)
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('tier-seg').filter({ hasText: 'Archive' }).click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });
    await expect.poll(async () => {
      const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
      return jobs.find((j: { kind: string }) => j.kind === 'archive')?.status;
    }, { timeout: 180_000 }).toBe('completed');

    // 2) clear localPath so the restore writes to a fresh temp dir (the file isn't already present →
    //    it must actually be restored, which classifies its archive-tier chunk as needing rehydration)
    await request.patch(`/api/repos/${created.id}`, { data: { localPath: '' } });

    // 3) whole-repo restore from the header → drawer dismisses → pill appears
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-restore').click();
    await page.getByTestId('drawer-start').click();

    // 4) open the restore job's detail page — the cost modal renders there (moved off the drawer),
    //    with both priority options
    let restoreId: string | undefined;
    await expect.poll(async () => {
      const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
      restoreId = jobs.find((j: { kind: string }) => j.kind === 'restore')?.id;
      return restoreId;
    }, { timeout: 120_000 }).toBeTruthy();

    await page.goto(`/jobs/${restoreId}`);
    await expect(page.getByTestId('cost-modal')).toBeVisible({ timeout: 120_000 });
    await expect(page.getByTestId('prio-standard')).toBeVisible();
    await expect(page.getByTestId('prio-high')).toBeVisible();

    // 5) decline — no real rehydration is requested
    await page.getByTestId('cost-decline').click();
    await expect(page.getByTestId('cost-modal')).toBeHidden();
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
