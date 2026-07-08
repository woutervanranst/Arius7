import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect } from '../support/fixtures';
import { scratchContainer } from '../support/scratch';

// Destructive: archives a fresh scratch container twice (a second file is added between runs so the
// root hash changes), then verifies the time-travel picker lists snapshots newest-first — the newest
// (v2) on top, carrying LATEST and selected by default; v1 = the first/oldest, at the bottom. A single
// snapshot can't catch the inversion this guards against, hence two real archives.
//
// Uses a per-run-unique container: repo deletion leaves the Azure blobs behind, and re-archiving a
// reused scratch container yields a nondeterministic snapshot count, so each run starts clean.
test('snapshot picker numbers snapshots newest-first with the newest as LATEST + default @write', async ({ page, request, repo }) => {
  test.skip(!process.env.ARIUS_E2E_WRITE, 'set ARIUS_E2E_WRITE=1 to run the destructive two-snapshot archive');
  test.setTimeout(420_000);

  const src = fs.mkdtempSync(path.join(os.tmpdir(), 'arius-e2e-snaporder-'));
  fs.writeFileSync(path.join(src, 'first.txt'), `arius e2e snap1 ${Date.now()}`);

  const created = await (await request.post('/api/repos', {
    data: { accountId: repo.accountId, container: scratchContainer(`snaporder-${Date.now()}`), alias: 'E2E Snapshot Order', passphrase: 'e2etest', localPath: src, defaultTier: 'cold' },
  })).json();

  // Confirm completion from the backend (the new snapshot lands), not the live "Archive complete"
  // toast: a sub-second archive can finish before the hub re-subscribes, so that UI event races away
  // and never shows.
  const archive = async (expectedSnapshots: number) => {
    await page.goto(`/repos/${created.id}/files`);
    await page.getByTestId('btn-archive').click();
    await page.getByTestId('drawer-start').click();
    await expect.poll(async () => {
      const res = await request.get(`/api/repos/${created.id}/snapshots`);
      if (!res.ok()) return 0;
      const s = await res.json();
      return Array.isArray(s) ? s.length : 0;
    }, { timeout: 180_000, message: `expected ${expectedSnapshots} snapshot(s)` }).toBeGreaterThanOrEqual(expectedSnapshots);
  };

  try {
    await archive(1);                                                // snapshot 1
    fs.writeFileSync(path.join(src, 'second.txt'), `arius e2e snap2 ${Date.now()}`);
    await archive(2);                                                // snapshot 2 (root hash changed)

    // Wait until the backend lists both snapshots before reading the UI.
    await expect.poll(async () => {
      const s = await (await request.get(`/api/repos/${created.id}/snapshots`)).json();
      return Array.isArray(s) ? s.length : 0;
    }, { timeout: 30_000 }).toBe(2);

    // Default (picker closed): the newest snapshot is selected — labelled v2, LATEST, live.
    await page.goto(`/repos/${created.id}/files`);
    const picker = page.getByTestId('snapshot-picker');
    await expect(picker).toContainText('v2');
    await expect(picker).toContainText('LATEST');
    await expect(page.getByText('Live working state')).toBeVisible();

    // Open the picker: newest-first → v2 on top carrying LATEST, v1 at the bottom (not LATEST).
    await picker.click();
    const items = page.getByTestId('snapshot-item');
    await expect(items).toHaveCount(2);
    await expect(items.nth(0)).toContainText('v2');
    await expect(items.nth(0)).toContainText('LATEST');
    await expect(items.nth(1)).toContainText('v1');
    await expect(items.nth(1)).not.toContainText('LATEST');

    // Selecting the last (oldest, v1) drops into the historical view.
    await items.nth(1).click();
    await expect(page.getByText('Historical view')).toBeVisible();
  } finally {
    await request.delete(`/api/repos/${created.id}`);
    fs.rmSync(src, { recursive: true, force: true });
  }
});
