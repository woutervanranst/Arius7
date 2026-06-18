import { defineConfig, devices } from '@playwright/test';
import * as path from 'path';

const stateDir = path.resolve(__dirname, 'e2e/.state');

/**
 * Live full-stack E2E: Playwright boots the real Arius.Api + ng serve (proxying REST + SignalR) and
 * drives the app against a configured repository. Serial (workers: 1) because the specs share one
 * backend + repository and some mutate it (archive / localPath / schedules).
 */
export default defineConfig({
  testDir: './e2e/specs',
  globalSetup: './e2e/support/global-setup.ts',
  globalTeardown: './e2e/support/global-teardown.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [
    ['list'],
    ['html', { outputFolder: 'e2e/playwright-report', open: 'never' }],
    // In CI, write a results table to the GitHub Actions job summary (mirrors the .NET test jobs).
    ...(process.env.GITHUB_ACTIONS
      ? [['@estruyf/github-actions-reporter', { title: 'Playwright e2e', useDetails: true }] as const]
      : []),
  ],
  outputDir: 'e2e/test-results',
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: 20_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project ../Arius.Api',
      url: 'http://localhost:5080/api/health',
      reuseExistingServer: true,
      timeout: 180_000,
      stdout: 'pipe',
      env: {
        ASPNETCORE_URLS: 'http://localhost:5080',
        ASPNETCORE_ENVIRONMENT: 'Development',
        Arius__AppDbPath: path.join(stateDir, 'arius-e2e.sqlite'),
        Arius__DataProtectionKeysPath: path.join(stateDir, 'keys'),
      },
    },
    {
      command: 'npm start',
      url: 'http://localhost:4200',
      reuseExistingServer: true,
      timeout: 180_000,
    },
  ],
});
