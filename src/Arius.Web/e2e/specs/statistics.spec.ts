import { test, expect } from '../support/fixtures';

test('statistics tab shows the four KPI cards with real figures', async ({ page, repo }) => {
  await page.goto(`/repos/${repo.repoId}/statistics`);
  const cards = page.getByTestId('kpi-card');
  await expect(cards).toHaveCount(4, { timeout: 30_000 });

  // every card shows a real figure, not just the placeholder dash
  for (let i = 0; i < 4; i++) {
    await expect(cards.nth(i)).toContainText(/\d/);
    await expect(cards.nth(i)).not.toContainText('—');
  }

  await expect(page.getByText('Unique chunks')).toBeVisible();
});
