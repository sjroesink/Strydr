import { expect, test } from '@playwright/test';
import { readFile, writeFile } from 'node:fs/promises';

async function controlled(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.evaluate(async () => { await navigator.serviceWorker.ready; });
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
}

test('real production worker: install identity, public-only cache, warm offline refresh and reconnect', async ({ page, context }, info) => {
  await controlled(page);
  const client = await context.newCDPSession(page);
  const { data, errors } = await client.send('Page.getAppManifest');
  expect(errors).toEqual([]);
  expect(data).toBeTruthy(); const manifest = JSON.parse(data!);
  expect(manifest).toMatchObject({ name: 'Meander', display: 'standalone', start_url: '/', scope: '/' });
  for (const icon of manifest.icons) {
    const dimensions = await page.evaluate(async src => {
      const image = new Image(); image.src = src; await image.decode();
      return [image.naturalWidth, image.naturalHeight];
    }, icon.src);
    expect(dimensions).toEqual(icon.sizes.split('x').map(Number));
  }
  const workerResponse = await page.request.get('/sw.js');
  expect(workerResponse.headers()['cache-control']).toBe('no-cache');
  expect(workerResponse.headers()['content-type']).toContain('javascript');
  expect((await page.request.get('/manifest.webmanifest')).headers()['content-type']).toContain('application/manifest+json');
  // Chromium versions differ on whether isolated contexts report the incognito restriction.
  // Neither a valid PWA nor this environment-only restriction is an installation defect.
  // Keep rejecting every actual manifest/SW error; the persistent-context probe requires zero errors.
  expect((await client.send('Page.getInstallabilityErrors')).installabilityErrors.filter(error => error.errorId !== 'in-incognito')).toEqual([]);
  await page.evaluate(async () => {
    await fetch('/api/connections');
    await fetch('/api/searches/00000000-0000-0000-0000-000000000001');
    await fetch('/map/not-cached.pmtiles');
  });
  const cachePaths = await page.evaluate(async () => {
    const paths: string[] = [];
    for (const name of await caches.keys()) for (const request of await (await caches.open(name)).keys()) paths.push(new URL(request.url).pathname);
    return paths;
  });
  expect(cachePaths).toContain('/index.html');
  expect(cachePaths.some(path => /^\/(api|map|routes)\//.test(path))).toBe(false);
  expect(cachePaths.every(path => /^\/(?:assets\/|icons\/|index\.html$|manifest\.webmanifest$|favicon\.svg$|apple-touch-icon\.png$)/.test(path))).toBe(true);
  await page.getByRole('button', { name: 'NL', exact: true }).click();
  await context.setOffline(true);
  // Current Chromium separates request emulation from navigator state. Use its native
  // protocol override as well; the real blocked fetch below still proves network loss.
  await client.send('Network.overrideNetworkState', { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  expect(await page.evaluate(() => navigator.onLine)).toBe(false);
  await page.reload();
  expect(await page.evaluate(() => navigator.onLine)).toBe(false);
  await expect(page.getByRole('heading', { name: 'Waar begin je?' })).toBeVisible();
  await expect(page.locator('.pwa-status [role="status"]')).toContainText('Je bent offline');
  await expect(page.getByRole('combobox').first()).toBeDisabled();
  await expect(page.getByRole('button', { name: 'NL', exact: true })).toHaveAttribute('aria-pressed', 'true');
  expect(await page.evaluate(() => fetch('/api/connections').then(() => false, () => true))).toBe(true);
  await page.screenshot({ path: info.outputPath('offline-shell.png'), fullPage: true });
  await page.goto('/routes/00000000-0000-0000-0000-000000000001?route=private');
  await expect(page.locator('.pwa-status [role="status"]')).toContainText('Je bent offline');
  await expect(page.getByRole('heading', { name: 'Waar begin je?' })).toBeVisible();
  await context.setOffline(false);
  await client.send('Network.overrideNetworkState', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await expect(page.locator('.pwa-status [role="status"]')).toHaveCount(0);
});

test('normal persistent browser passes installability checks without claiming an OS installation', async ({ playwright }, info) => {
  const context = await playwright.chromium.launchPersistentContext(info.outputPath('installability-profile'), {
    executablePath: process.env.CHROMIUM_EXECUTABLE_PATH || undefined, headless: true, baseURL: 'http://127.0.0.1:5180',
    args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'],
  });
  try {
    const page = await context.newPage(); await controlled(page);
    const client = await context.newCDPSession(page);
    const result = await client.send('Page.getInstallabilityErrors');
    expect(result.installabilityErrors).toEqual([]);
    const { data, errors } = await client.send('Page.getAppManifest');
    expect(errors).toEqual([]); expect(data).toBeTruthy(); expect(JSON.parse(data!).display).toBe('standalone');
    const evidence = { ...(await client.send('Browser.getVersion')), ...result, manifest: JSON.parse(data!), scope: await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.scope) };
    const evidencePath = info.outputPath('installability.json');
    await writeFile(evidencePath, JSON.stringify(evidence, null, 2));
    await info.attach('installability.json', { path: evidencePath, contentType: 'application/json' });
  } finally { await context.close(); }
});

test('waiting updates never force reload; explicit consent updates only the initiating tab', async ({ page, context }) => {
  await controlled(page);
  const other = await context.newPage(); await other.goto('/'); await page.bringToFront();
  await page.getByRole('button', { name: 'EN', exact: true }).click();
  let navigations = 0; let otherNavigations = 0;
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  other.on('framenavigated', frame => { if (frame === other.mainFrame()) otherNavigations++; });
  const path = 'dist/sw.js'; const original = await readFile(path, 'utf8');
  try {
    // Fixture-only new worker version; no production mutation or private route data.
    await writeFile(path, original.replace(/meander-shell-([\w-]+)/, 'meander-shell-e2e-$1'));
    await page.evaluate(async () => { await (await navigator.serviceWorker.getRegistration())!.update(); });
    const update = page.getByRole('button', { name: 'Update and reload' });
    await expect(update).toBeVisible();
    expect(navigations).toBe(0); expect(otherNavigations).toBe(0);
    page.once('dialog', dialog => dialog.dismiss()); await update.click();
    expect(navigations).toBe(0);
    page.once('dialog', dialog => dialog.accept());
    await Promise.all([page.waitForEvent('framenavigated', frame => frame === page.mainFrame()), update.click()]);
    await page.waitForLoadState('domcontentloaded');
    await expect.poll(() => navigations).toBe(1);
    expect(otherNavigations).toBe(0);
    await expect(page.getByRole('heading', { name: 'Where do you start?' })).toBeVisible();
    const names = await page.evaluate(() => caches.keys());
    expect(names.filter(name => name.startsWith('meander-shell-')).length).toBeLessThanOrEqual(2);
  } finally { await writeFile(path, original); }
});
