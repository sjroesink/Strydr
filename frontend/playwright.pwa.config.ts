import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './pwa-tests', workers: 1, timeout: 45000,
  use: { baseURL: 'http://127.0.0.1:5180', trace: 'retain-on-failure', screenshot: 'only-on-failure',
    launchOptions: { executablePath: process.env.CHROMIUM_EXECUTABLE_PATH || undefined, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] } },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['Pixel 7'], defaultBrowserType: 'chromium' } },
  ],
  webServer: [
    { command: 'node e2e/upstream.mjs', url: 'http://127.0.0.1:5091/health', reuseExistingServer: false },
    { command: 'dotnet run --project ../src/Meander.Api/Meander.Api.csproj --no-launch-profile --urls http://127.0.0.1:5180 -- --webroot ../../frontend/dist',
      url: 'http://127.0.0.1:5180/api/health', reuseExistingServer: false, timeout: 60000,
      env: { ASPNETCORE_ENVIRONMENT: 'Production', Ors__BaseUrl: 'http://127.0.0.1:5091/', Ors__ApiKey: '',
        ResultStorage__DatabasePath: ':memory:', PublicBaseUrl: 'http://127.0.0.1:5180', DataProtection__KeysPath: '' } },
  ],
});
