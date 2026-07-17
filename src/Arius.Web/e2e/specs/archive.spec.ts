import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

test.describe('archive drawer', () => {
  test('idle form: four tier segments + on-disk radio + fast-hash toggle', async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);
    await page.getByTestId('btn-archive').click();
    await expect(page.getByTestId('drawer-title')).toContainText('Archive');
    await expect(page.getByTestId('tier-seg')).toHaveCount(4);

    // on-disk radio: three options, defaults to 'keep'
    const onDiskButtons = page.getByTestId('seg-on-disk');
    await expect(onDiskButtons).toHaveCount(3);
    const keepBtn         = page.locator('[data-testid="seg-on-disk"][data-on-disk="keep"]');
    const keepPtrBtn      = page.locator('[data-testid="seg-on-disk"][data-on-disk="keep-pointers"]');
    const replaceBtn      = page.locator('[data-testid="seg-on-disk"][data-on-disk="replace"]');
    await expect(keepBtn).toHaveClass(/on/);
    await expect(keepPtrBtn).not.toHaveClass(/on/);
    await expect(replaceBtn).not.toHaveClass(/on/);

    await keepPtrBtn.click();
    await expect(keepBtn).not.toHaveClass(/on/);
    await expect(keepPtrBtn).toHaveClass(/on/);

    await replaceBtn.click();
    await expect(replaceBtn).toHaveClass(/on/);
    await expect(keepPtrBtn).not.toHaveClass(/on/);

    // fast-hash toggle: unchecked by default, can be checked
    await expect(page.getByTestId('toggle-fast-hash')).not.toBeChecked();
    await page.getByTestId('toggle-fast-hash').check();
    await expect(page.getByTestId('toggle-fast-hash')).toBeChecked();

    await page.getByRole('button', { name: 'Close' }).click();
    await expect(page.getByTestId('drawer')).toBeHidden();
  });

  // Destructive: creates a dedicated container so the main repo's data is never replaced. Start
  // dismisses the drawer and hands the job to the floating pill — progress and completion are
  // observed on the pill / job history.
  test('real archive of a temp folder streams to completion @write', async ({ page, request, repo }) => {
    test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive archive flow');
    test.setTimeout(200_000);

    const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-archive-'));
    fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);
    fs.writeFileSync(path.join(src, 'notes.md'), '# notes\n'.repeat(50));

    const created = await (await request.post('/api/repos', {
      data: { accountId: repo.accountId, container: scratchContainer(`write-${Date.now()}`), alias: 'E2E Write Target', passphrase: 'e2etest', localPath: src, defaultTier: 'hot' },
    })).json();

    try {
      await page.goto(`/repos/${created.id}/files`);
      await page.getByTestId('btn-archive').click();
      await page.getByTestId('drawer-start').click();
      await expect(page.getByTestId('drawer')).toBeHidden();
      await expect(page.getByTestId('job-pill')).toBeVisible({ timeout: 60_000 });

      await expect.poll(async () => {
        const jobs = await (await request.get(`/api/jobs?repositoryId=${created.id}`)).json();
        return jobs.find((j: { kind: string }) => j.kind === 'archive')?.status;
      }, { timeout: 180_000 }).toBe('completed');
    } finally {
      await request.delete(`/api/repos/${created.id}`);
      fs.rmSync(src, { recursive: true, force: true });
    }
  });
});
