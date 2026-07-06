import { defineConfig, devices } from '@playwright/test';
import * as path from 'path';

const stateDir = path.resolve(__dirname, 'e2e/hermetic/.state');

/**
 * Hermetic browser E2E: Playwright boots the real Arius.Api pipeline with a SCRIPTED Arius.Core
 * (the Arius.Api.Testing host) + ng serve (proxying REST + SignalR) — no Azure. Specs seed a repo +
 * pick a named scenario over /api/testing, then drive the real UI. Serial (workers: 1); each spec
 * resets the app db via the control endpoint for isolation.
 */
export default defineConfig({
  testDir: './e2e/hermetic/specs',
  globalSetup: './e2e/hermetic/support/global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [
    ['list'],
    ['html', { outputFolder: 'e2e/hermetic/playwright-report', open: 'never' }],
    ...(process.env.GITHUB_ACTIONS
      ? [['@estruyf/github-actions-reporter', { title: 'Playwright e2e (hermetic)', useDetails: true }] as const]
      : []),
  ],
  outputDir: 'e2e/hermetic/test-results',
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: 20_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project ../Arius.Api.Testing',
      url: 'http://localhost:5080/api/health',
      reuseExistingServer: false,   // always boot the SCRIPTED host, not a stray real Api on :5080
      timeout: 180_000,
      stdout: 'pipe',
      env: {
        ASPNETCORE_URLS: 'http://localhost:5080',
        Arius__AppDbPath: path.join(stateDir, 'arius-hermetic.sqlite'),
        Arius__DataProtectionKeysPath: path.join(stateDir, 'keys'),
      },
    },
    {
      command: 'npm start',
      url: 'http://localhost:4200',
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
  ],
});
