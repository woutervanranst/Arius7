import { defineConfig, devices } from '@playwright/test';

/**
 * Front-end-only E2E: boots `ng serve` and drives the real Angular app while mocking the REST
 * endpoints and the SignalR hub at the browser network boundary (see e2e/mock/signalr-mock.ts).
 * No Arius.Api / Azure backend required — used to verify pure front-end behaviour (e.g. global
 * search → reveal & highlight) in a real browser.
 */
export default defineConfig({
  testDir: './e2e/mock',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [['list']],
  outputDir: 'e2e/test-results-mock',
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: 20_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'npm start',
      url: 'http://localhost:4200',
      reuseExistingServer: true,
      timeout: 180_000,
    },
  ],
});
