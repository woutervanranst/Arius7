import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Destructive: archives a small folder to a dedicated container, then restores it twice to contrast
// the two restore-destination behaviours. Start dismisses the drawer and hands the job to the floating
// pill — progress, the cost modal and the restored-file count are all observed on the pill / the job's
// `/jobs/:id` detail page.
test('restore writes files to an empty destination, and skips them when already present @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive restore round-trip');
  test.setTimeout(300_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-roundtrip-'));
  fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);
  fs.writeFileSync(path.join(src, 'notes.md'), '# notes\n'.repeat(50));

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`roundtrip-${Date.now()}`), alias: 'E2E Round-trip', passphrase: 'e2etest', localPath: src, defaultTier: 'hot' },
  })).json();

  // Poll the repo's job list for the newest job of `kind` that isn't `excludeId` (so scenario B's
  // restore isn't confused with scenario A's already-completed one — /api/jobs is newest-first).
  const waitForNewJobId = async (kind: string, excludeId?: string): Promise<string> => {
    let id: string | undefined;
    await expect.poll(async () => {
      const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
      id = jobs.find((j: { kind: string; id: string }) => j.kind === kind && j.id !== excludeId)?.id;
      return id;
    }, { timeout: 60_000 }).toBeTruthy();
    return id!;
  };
  const waitForJobStatus = (id: string, status: string) =>
    expect.poll(async () => (await (await request.get(`/api/jobs/${id}`)).json()).status, { timeout: 180_000 }).toBe(status);
  const filesRestoredOf = async (id: string): Promise<number> => {
    const job = await (await request.get(`/api/jobs/${id}`)).json();
    return job.outcome ? (JSON.parse(job.outcome).filesRestored ?? 0) : 0;
  };

  try {
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('drawer')).toBeHidden();
    await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });
    const archiveId = await waitForNewJobId('archive');
    await waitForJobStatus(archiveId, 'completed');

    // ── Scenario A: empty destination → the files are actually restored ──────
    await request.patch(`/api/repos/${created.id}`, { data: { localPath: '' } });
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-restore').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('drawer')).toBeHidden();

    const restoreAId = await waitForNewJobId('restore');
    // An online (Hot-tier) restore surfaces an estimated-cost approval modal before downloading;
    // approve it to let the download proceed. (No rehydration here, so there is no priority choice.)
    await page.goto(`/jobs/${restoreAId}`);
    await expect(page.getByTestId('cost-modal')).toBeVisible({ timeout: 120_000 });
    await page.getByTestId('cost-approve').click();
    await waitForJobStatus(restoreAId, 'completed');
    expect(await filesRestoredOf(restoreAId)).toBe(2);   // both files were downloaded

    // ── Scenario B: destination already holds identical files → all skipped ──
    await request.patch(`/api/repos/${created.id}`, { data: { localPath: src } });
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-restore').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('drawer')).toBeHidden();

    const restoreBId = await waitForNewJobId('restore', restoreAId);
    await waitForJobStatus(restoreBId, 'completed');
    expect(await filesRestoredOf(restoreBId)).toBe(0);   // nothing to restore (identical)
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
