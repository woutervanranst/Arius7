import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// The /jobs overview (design README §Screens 5, mockup 4a) replaces the old runs table + global
// live console with three sections: Needs your attention (awaiting-cost), Active (live mini-bar +
// Reattach ›) and Scheduled & history (one-line outcome). No console anywhere on this page anymore.
test('jobs screen renders the sectioned overview, no live console', async ({ page }) => {
  await page.goto('/jobs');
  await expect(page.getByRole('heading', { name: 'Jobs' })).toBeVisible();
  await expect(page.getByText('running')).toBeVisible();   // the "N running" chip
  await expect(page.getByText('waiting')).toBeVisible();   // the "N waiting" chip
  await expect(page.getByText('scheduled')).toBeVisible(); // the "N scheduled" chip

  // the global console is gone — from this page and everywhere else
  await expect(page.getByText('Live output')).toHaveCount(0);
  await expect(page.getByTestId('live-console')).toHaveCount(0);

  // Active and Scheduled & history sections always render (with an empty-state message when there's no data)
  await expect(page.getByText('Active', { exact: true })).toBeVisible();
  await expect(page.getByTestId('jobs-active')).toBeVisible();
  await expect(page.getByText('Scheduled & history')).toBeVisible();
  await expect(page.getByTestId('jobs-history')).toBeVisible();

  // any Active/History rows present (from prior runs) carry a status pill (the amber banner rows don't)
  const statusRows = page.getByTestId('jobs-active').getByTestId('job-row').or(page.getByTestId('jobs-history').getByTestId('job-row'));
  if (await statusRows.count() > 0) {
    await expect(statusRows.first().getByTestId('job-status')).toBeVisible();
  }

  // "Needs your attention" only renders when there's an awaiting-cost job
  if (await page.getByTestId('jobs-needs-attention').count() > 0) {
    await expect(page.getByTestId('job-review-cost').first()).toBeVisible();
  }
});

// Destructive: creates a dedicated container so the main repo's data is never replaced (scoped like pill.spec.ts).
test('Detail › on an active row opens the job detail page @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive archive flow');
  test.setTimeout(120_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-jobsoverview-'));
  fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`jobsoverview-${Date.now()}`), alias: 'E2E Jobs Overview', passphrase: 'e2etest', localPath: src, defaultTier: 'hot' },
  })).json();

  try {
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });

    let jobId: string | undefined;
    await expect.poll(async () => {
      const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
      jobId = jobs.find((j: { kind: string }) => j.kind === 'archive')?.id;
      return jobId;
    }, { timeout: 60_000 }).toBeTruthy();

    // The row for this job — Active or History both expose a "Detail ›" link to the same job detail page.
    await page.goto('/jobs');
    const row = page.getByTestId('job-row').filter({ hasText: 'E2E Jobs Overview' });
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.getByRole('link', { name: /Detail/ }).click();
    await expect(page).toHaveURL(new RegExp(`/jobs/${jobId}$`));
    await expect(page.getByTestId('job-detail')).toBeVisible();
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
