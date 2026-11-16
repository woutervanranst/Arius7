import { test, expect } from '../support/fixtures';

const files = [
  { path: 'photos/a.jpg', size: 1000 },
  { path: 'docs/b.txt', size: 500 },
];

test('a finished download offers its files as one zip and cleans up on demand', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'dl' });
  await control.scenario(repoId, 'onlineDownload');
  const jobId = await control.startDownload(repoId, files);

  await expect.poll(async () => (await (await request.get(`/api/jobs/${jobId}`)).json()).status).toBe('awaiting-cost');

  await page.goto(`/jobs/${jobId}`);
  await expect(page.getByTestId('cost-modal')).toContainText('Confirm download cost');
  await expect(page.getByTestId('cost-approve')).toHaveText('Download');
  await page.getByTestId('cost-approve').click();

  await expect(page.getByTestId('download-card')).toContainText('All 2 files ready');
  const download = page.waitForEvent('download');
  await page.getByTestId('download-all').click();
  expect((await download).suggestedFilename()).toMatch(/^dl-\d{4}-\d{2}-\d{2}\.zip$/);
  await expect(page.getByTestId('download-file-row').first()).toContainText('Downloaded');
  await expect(page.getByTestId('download-file-row').last()).toContainText('Downloaded');

  await page.getByTestId('download-cleanup').click();
  await expect(page.getByTestId('download-card')).toContainText('Files cleaned up');
});

test('a rehydrating download offers the ready file now', async ({ page, request, control }) => {
  const repoId = await control.seedRepo({ alias: 'dl-rehydrating' });
  await control.scenario(repoId, 'rehydratingDownloadStaysPending');
  const jobId = await control.startDownload(repoId, files);

  await expect.poll(async () => (await (await request.get(`/api/jobs/${jobId}`)).json()).status).toBe('awaiting-cost');

  await page.goto(`/jobs/${jobId}`);
  await page.getByTestId('cost-approve').click();
  await expect.poll(async () => (await (await request.get(`/api/jobs/${jobId}`)).json()).status).toBe('rehydrating');

  await expect(page.getByTestId('download-ready')).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByTestId('download-ready').click();
  expect((await download).suggestedFilename()).toBe('a.jpg');
  await expect(page.getByTestId('download-file-row').filter({ hasText: 'photos/a.jpg' })).toContainText('Downloaded');
  await expect(page.getByText('prepares the rest by itself')).toBeVisible();
});

test('settings saves the download clean-up period and cleans up all now', async ({ page, request, control }) => {
  // A finished download leaves files on the server for "Clean up all now" to remove.
  const repoId = await control.seedRepo({ alias: 'dl-settings' });
  await control.scenario(repoId, 'onlineDownload');
  const jobId = await control.startDownload(repoId, files);
  await expect.poll(async () => (await (await request.get(`/api/jobs/${jobId}`)).json()).status).toBe('awaiting-cost');
  await page.goto(`/jobs/${jobId}`);
  await page.getByTestId('cost-approve').click();
  await expect.poll(async () => (await (await request.get(`/api/jobs/${jobId}`)).json()).status).toBe('completed');

  await page.goto('/settings');
  await expect(page.getByRole('heading', { name: 'Archive' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Restore' })).toBeVisible();

  await page.getByTestId('cleanup-days').fill('3');
  await expect(page.getByTestId('defaults-save')).toBeEnabled();
  await page.getByTestId('defaults-save').click();

  await page.reload();
  await expect(page.getByTestId('cleanup-days')).toHaveValue('3');

  await expect(page.getByTestId('download-usage')).toHaveText(/^1 download /);
  await page.getByTestId('cleanup-all').click();
  await expect(page.getByTestId('download-usage')).toContainText('0 downloads');
});
