import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Destructive: archives a small folder to a dedicated container, then restores it twice to contrast
// the two restore-destination behaviours.
test('restore writes files to an empty destination, and skips them when already present @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive restore round-trip');
  test.setTimeout(300_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-roundtrip-'));
  fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);
  fs.writeFileSync(path.join(src, 'notes.md'), '# notes\n'.repeat(50));

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer('roundtrip'), alias: 'E2E Round-trip', passphrase: 'e2etest', localPath: src, defaultTier: 'hot' },
  })).json();

  try {
    // archive the source folder
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByText('Archive complete', { exact: false })).toBeVisible({ timeout: 180_000 });
    await page.getByRole('button', { name: 'Close' }).click();

    // ── Scenario A: empty destination → the files are actually restored ──────
    await request.patch(`/api/repos/${created.id}`, { data: { localPath: '' } });
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-restore').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByTestId('live-console')).toContainText('hello.txt', { timeout: 150_000 });   // a file was downloaded
    await expect(page.getByText('Restore complete.')).toBeVisible();
    await page.getByRole('button', { name: 'Close' }).click();

    // ── Scenario B: destination already holds identical files → all skipped ──
    await request.patch(`/api/repos/${created.id}`, { data: { localPath: src } });
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-restore').click();
    await page.getByTestId('drawer-start').click();
    await expect(page.getByText('Restore complete.')).toBeVisible({ timeout: 150_000 });
    await expect(page.getByTestId('live-console')).toContainText('0 files');     // nothing to restore (identical)
    await expect(page.getByTestId('live-console')).not.toContainText('hello.txt');
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
