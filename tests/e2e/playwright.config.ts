import { defineConfig, devices } from '@playwright/test';
import { BASE_URL } from './support/env';
import { authFile } from './support/state';

/*
 * End-to-end tests of the web console against a running BerMap server and
 * PostgreSQL. Locally, PW_CHROME_PATH runs them in an installed Google Chrome
 * (e.g. /Applications/Google Chrome.app/Contents/MacOS/Google Chrome);
 * without it, Playwright's bundled Chromium is used (CI).
 */
const chromePath = process.env.PW_CHROME_PATH?.trim();
const isCI = !!process.env.CI;

export default defineConfig({
  testDir: './specs',
  globalSetup: './global-setup.ts',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  workers: isCI ? 2 : 4,
  reporter: isCI ? [['list'], ['html', { open: 'never' }], ['github']] : [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: BASE_URL,
    storageState: authFile('admin'),
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    colorScheme: 'light',
    acceptDownloads: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Video needs Playwright's ffmpeg, which CI installs next to the bundled Chromium.
    video: isCI && !chromePath ? 'retain-on-failure' : 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
        launchOptions: chromePath ? { executablePath: chromePath } : {},
      },
    },
  ],
});
