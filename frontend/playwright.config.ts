import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1, timeout: 30000,
  reporter: [['list']],
  use: { baseURL: 'http://127.0.0.1:5174', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 }, launchOptions: { args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] } } },
    { name: 'mobile', use: { ...devices['Pixel 7'], defaultBrowserType: 'chromium', launchOptions: { args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] } } },
  ],
  webServer: [
    { command: 'node e2e/upstream.mjs', url: 'http://127.0.0.1:5091/health', reuseExistingServer: false },
    {
      command: 'dotnet run --project ../src/Meander.Api/Meander.Api.csproj --no-launch-profile --urls http://127.0.0.1:5081',
      url: 'http://127.0.0.1:5081/api/health', reuseExistingServer: false, timeout: 60000,
      env: { Ors__ApiKey: 'e2e-fixture-key', Ors__BaseUrl: 'http://127.0.0.1:5091/', Ors__RetryCount: '0',
        RateLimiting__GeneratePerMinute: '100', Ors__DirectionsPerMinute: '1000', ASPNETCORE_ENVIRONMENT: 'Development',
        ResultStorage__DatabasePath: ':memory:', Woodland__Url: 'http://127.0.0.1:5091/woodland' },
    },
    {
      command: 'npm run dev -- --port 5174', url: 'http://127.0.0.1:5174', reuseExistingServer: false,
      env: { API_PROXY_TARGET: 'http://127.0.0.1:5081', VITE_MAP_STYLE_URL: 'http://127.0.0.1:5091/style.json',
        VITE_MAP_STYLE_DARK_URL: 'http://127.0.0.1:5091/style-dark.json' },
    },
  ],
});
