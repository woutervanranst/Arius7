import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

test.describe('archive drawer', () => {
  test('idle form: four tier segments + mutually-exclusive toggles', async ({ page, repo }) => {
    await page.goto(`/repos/${repo.repoId}/files`);
    await page.getByTestId('btn-archive').click();
    await expect(page.getByTestId('drawer-title')).toContainText('Archive');
    await expect(page.getByTestId('tier-seg')).toHaveCount(4);

    // --remove-local and --no-pointers are mutually exclusive
    await page.getByTestId('toggle-remove-local').check();
    await expect(page.getByTestId('toggle-remove-local')).toBeChecked();
    await page.getByTestId('toggle-no-pointers').check();
    await expect(page.getByTestId('toggle-no-pointers')).toBeChecked();
    await expect(page.getByTestId('toggle-remove-local')).not.toBeChecked();

    await page.getByRole('button', { name: 'Close' }).click();
    await expect(page.getByTestId('drawer')).toBeHidden();
  });

  // Destructive: creates a dedicated container so the main repo's data is never replaced.
  test('real archive of a temp folder streams to completion @write', async ({ page, request, repo }) => {
    test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive archive flow');
    test.setTimeout(200_000);

    const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-archive-'));
    fs.writeFileSync(path.join(src, 'hello.txt'), `arius e2e ${Date.now()}`);
    fs.writeFileSync(path.join(src, 'notes.md'), '# notes\n'.repeat(50));

    const created = await (await request.post('/api/repos', {
      data: { accountId: repo.accountId, container: scratchContainer(`write-${Date.now()}`), alias: 'E2E Write Target', passphrase: 'e2etest', localPath: src, defaultTier: 'cold' },
    })).json();

    try {
      await page.goto(`/repos/${created.id}/files`);
      await page.getByTestId('btn-archive').click();
      await page.getByTestId('drawer-start').click();
      await expect(page.getByText('Archive complete', { exact: false })).toBeVisible({ timeout: 180_000 });
    } finally {
      await request.delete(`/api/repos/${created.id}`);
      fs.rmSync(src, { recursive: true, force: true });
    }
  });
});
