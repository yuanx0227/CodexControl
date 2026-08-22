import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 8_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['line']],
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'mobile-chromium',
      use: { ...devices['Pixel 7'], channel: 'msedge' },
    },
    {
      name: 'iphone-webkit',
      use: { ...devices['iPhone 15'], browserName: 'webkit' },
    },
  ],
  webServer: [
    {
      command: 'npm run dev -- --port 5173',
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'dotnet run --project ../../tests/CodexControl.SystemHost/CodexControl.SystemHost.csproj --configuration Debug -- ../../out/system-host',
      url: 'http://127.0.0.1:5080/healthz',
      reuseExistingServer: false,
      timeout: 30_000,
    },
  ],
});
