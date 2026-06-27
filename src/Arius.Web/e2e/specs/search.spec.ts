import { test, expect } from '../support/fixtures';
import { revealFiles } from '../support/files';

test('global search returns cross-repository hits and reveals the clicked file', async ({ page, repo }) => {
  test.setTimeout(120_000);

  // Derive a real search term from a file in the repo (data-agnostic — the repo contents may change).
  const term = process.env.ARIUS_E2E_SEARCH ?? await deriveTerm(page, repo.repoId);

  await page.goto('/jobs');
  await page.getByTestId('topbar-search').click();
  await page.getByTestId('search-input').fill(term);

  const result = page.getByTestId('search-result').first();
  await expect(result).toBeVisible({ timeout: 60_000 });
  await expect(result).toContainText(repo.alias);

  await result.click();

  // Clicking a hit must navigate to the repo's Files tab AND carry the file path so the tab can
  // reveal it (regression guard for ARI-4: previously the click reset the tab to the repo root).
  await expect(page).toHaveURL(/\/repos\/\d+\/files\?path=/);

  // The clicked file's row must be revealed AND collected — its checkbox checked (row turns blue)
  // and it's added to the collector bar. Read the revealed path from the URL so the assertion is
  // data-agnostic regardless of which hit streamed first.
  const revealedPath = new URL(page.url()).searchParams.get('path');
  expect(revealedPath, 'navigation should include a ?path= for the clicked file').toBeTruthy();

  const revealedRow = page.locator(`[data-testid="file-row"][data-rel="${revealedPath}"]`);
  await expect(revealedRow).toBeVisible();
  await expect(revealedRow.locator('.ar-check.on')).toBeVisible();   // box is checked
  await expect(page.getByTestId('collected-bar')).toBeVisible();      // and it joined the collector
});

/** Opens the repo's file list (drilling into folders as needed) and returns a distinctive substring of the first file's name. */
async function deriveTerm(page: import('@playwright/test').Page, repoId: number): Promise<string> {
  const rows = await revealFiles(page, repoId);
  const text = (await rows.first().getByTestId('file-name').innerText()).trim();
  // a stable substring: filename without extension, capped, min 3 chars
  const base = (text.split('.')[0] || text).slice(0, 8);
  return base.length >= 3 ? base : text.slice(0, 4);
}
