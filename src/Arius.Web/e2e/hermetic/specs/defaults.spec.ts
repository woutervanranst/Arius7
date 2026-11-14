import { test, expect } from '../support/fixtures';

test('defaults flow down Factory -> Global settings -> Repository properties -> Run', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'photos' });

  // Global settings: pick keep-pointers, save.
  await page.goto('/settings');
  const globalArchive = page.getByTestId('defaults-archive');
  await globalArchive.locator('[data-testid="seg-on-disk"][data-on-disk="keep-pointers"]').click();
  await expect(globalArchive.locator('[data-option="onDisk"]').getByTestId('default-source')).toHaveText('Set in global settings');
  await page.getByTestId('defaults-save').click();
  await expect(page.getByTestId('defaults-save')).toBeDisabled();

  // The archive drawer inherits it from global settings; the tier still comes from the factory.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-archive').click();
  const drawer = page.getByTestId('drawer');
  await expect(drawer.locator('[data-testid="seg-on-disk"][data-on-disk="keep-pointers"]')).toHaveClass(/\bon\b/);
  await expect(drawer.locator('[data-option="onDisk"]').getByTestId('default-source')).toHaveText('Inherited from global settings');
  await expect(drawer.locator('[data-option="tier"]').getByTestId('default-source')).toHaveText('Inherited from factory defaults');
  await drawer.getByRole('button', { name: 'Close' }).click();

  // Repository properties: pick keep, save.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-properties').click();
  const repoArchive = page.getByTestId('properties-drawer').getByTestId('defaults-archive');
  await repoArchive.locator('[data-testid="seg-on-disk"][data-on-disk="keep"]').click();
  await expect(repoArchive.locator('[data-option="onDisk"]').getByTestId('default-source')).toHaveText('Set for this repository');
  await page.getByTestId('defaults-save').click();
  await expect(page.getByTestId('defaults-save')).toBeDisabled();
  await expect(page.getByTestId('schedule-summary').or(page.getByText('No schedules.'))).toBeVisible();

  // The drawer now inherits from the repository; a per-run change is marked and can be reset.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-archive').click();
  await expect(drawer.locator('[data-testid="seg-on-disk"][data-on-disk="keep"]')).toHaveClass(/\bon\b/);
  await expect(drawer.locator('[data-option="onDisk"]').getByTestId('default-source')).toHaveText('Inherited from repository properties');
  const fastHash = drawer.locator('[data-option="fastHash"]');
  await fastHash.getByTestId('toggle-fast-hash').check();
  await expect(fastHash.getByTestId('default-source')).toHaveText('Changed for this run');
  await fastHash.getByTestId('default-reset').click();
  await expect(fastHash.getByTestId('toggle-fast-hash')).not.toBeChecked();
  await expect(fastHash.getByTestId('default-source')).toHaveText('Inherited from factory defaults');
});
