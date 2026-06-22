import { test, expect } from '../support/fixtures';
import { revealFiles } from '../support/files';

test('global search returns cross-repository hits and navigates to the repo', async ({ page, repo }) => {
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
  await expect(page).toHaveURL(/\/repos\/\d+\/files/);
});

/** Opens the repo's file list (drilling into folders as needed) and returns a distinctive substring of the first file's name. */
async function deriveTerm(page: import('@playwright/test').Page, repoId: number): Promise<string> {
  const rows = await revealFiles(page, repoId);
  const text = (await rows.first().getByTestId('file-name').innerText()).trim();
  // a stable substring: filename without extension, capped, min 3 chars
  const base = (text.split('.')[0] || text).slice(0, 8);
  return base.length >= 3 ? base : text.slice(0, 4);
}
