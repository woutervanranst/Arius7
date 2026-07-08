import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Destructive: creates a dedicated container so the main repo's data is never replaced.
test('pill appears after Start and opens the job detail page @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive archive flow');
  test.setTimeout(120_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-pill-'));
  fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`pill-${Date.now()}`), alias: 'E2E Pill Target', passphrase: 'e2etest', localPath: src, defaultTier: 'hot' },
  })).json();

  try {
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('drawer-start').click();

    // Start dismisses the drawer and hands the jobId to the floating pill.
    await expect(page.getByTestId('drawer')).toBeHidden();
    await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });

    await page.getByTestId('pill-open').click();
    await expect(page).toHaveURL(/\/jobs\//);
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
