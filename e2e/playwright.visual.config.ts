import { defineConfig, devices } from '@playwright/test';

// Visual regression tests (G3, docs/DESIGN.md step 7): the map and the design system in Chromium, Firefox and WebKit,
// on a phone and a desktop. The screenshots differ between systems, so these run only inside the Playwright
// container (npm run test:visual), locally and in CI alike, and the baselines are that container's.
const phone = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2 };
const desktop = { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 };

export default defineConfig({
  testDir: './visual',
  outputDir: '../test-artifacts/visual/results',
  snapshotPathTemplate: '{testDir}/__screenshots__/{projectName}/{arg}{ext}',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: '../test-artifacts/visual/report', open: 'never' }],
  ],
  use: {
    baseURL: 'http://127.0.0.1:4180',
    locale: 'ru-RU',
    timezoneId: 'Europe/Moscow',
    contextOptions: { reducedMotion: 'reduce' },
    trace: 'retain-on-failure',
  },
  expect: {
    toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled', caret: 'hide' },
  },
  webServer: {
    command: 'node ../scripts/static-server.mjs ../web/dist 4180',
    url: 'http://127.0.0.1:4180/styleguide',
    reuseExistingServer: false,
    timeout: 30_000,
  },
  projects: [
    { name: 'chromium-desktop', use: { ...devices['Desktop Chrome'], ...desktop } },
    {
      name: 'chromium-phone',
      use: { ...devices['Pixel 7'], ...phone, isMobile: true, hasTouch: true },
    },
    { name: 'firefox-desktop', use: { ...devices['Desktop Firefox'], ...desktop } },
    // Firefox has no mobile emulation: the phone's size only
    { name: 'firefox-phone', use: { ...devices['Desktop Firefox'], ...phone } },
    { name: 'webkit-desktop', use: { ...devices['Desktop Safari'], ...desktop } },
    {
      name: 'webkit-phone',
      use: { ...devices['iPhone 13'], ...phone, isMobile: true, hasTouch: true },
    },
  ],
});
