import { expect, test, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';

// The planner floats over the map, so map clicks aim at the part left uncovered.
async function clickFreeMap(page: Page, fx: number, fy: number) {
  const area = await page.evaluate(() => {
    let top = 0, bottom = window.innerHeight, left = 0;
    const wide = window.matchMedia('(min-width: 760px)').matches;
    for (const element of document.querySelectorAll<HTMLElement>('[data-map-overlay]')) {
      const rect = element.getBoundingClientRect();
      if (!rect.width) continue;
      const kind = element.dataset.mapOverlay;
      const side = kind === 'sheet' ? wide ? 'left' : 'bottom' : kind === 'topbar' ? wide ? null : 'top' : kind;
      if (side === 'top') top = Math.max(top, rect.bottom);
      if (side === 'bottom') bottom = Math.min(bottom, rect.top);
      if (side === 'left') left = Math.max(left, rect.right);
    }
    return { top, bottom, left, right: window.innerWidth };
  });
  await page.mouse.click(area.left + (area.right - area.left) * fx, area.top + (area.bottom - area.top) * fy);
}

async function chooseUtrecht(page: Page, label = 'Start location') {
  await page.getByRole('combobox', { name: label }).fill('Utrecht');
  await page.getByRole('option', { name: 'Utrecht, Netherlands' }).click();
}

test('draw a fixed section with map clicks, export it, and complete it into a loop', async ({ page }, testInfo) => {
  let previewCalls = 0;
  let generationCalls = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/routes/preview' && request.method() === 'POST') previewCalls++;
    if (new URL(request.url()).pathname === '/api/routes/generate' && request.method() === 'POST') generationCalls++;
  });
  await page.route('https://fonts.googleapis.com/**', route => route.abort());
  await page.goto('/');
  await chooseUtrecht(page);
  await page.getByRole('button', { name: /Next: distance/ }).click();
  await page.getByLabel('How far?').fill('10');
  await page.getByRole('button', { name: /Next: preferences/ }).click();
  await expect(page.locator('.map-marker')).toHaveCount(1);
  await page.waitForTimeout(650);
  await page.getByRole('button', { name: /Pass through a spot/ }).click();
  await page.waitForTimeout(350);
  const previewPending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/routes/preview' && response.request().method() === 'POST');
  await clickFreeMap(page, .58, .58);
  await clickFreeMap(page, .68, .42);
  const previewResponse = await previewPending;
  expect(previewResponse.status()).toBe(200);
  let preview = await previewResponse.json();
  await expect(page.locator('.map-via-marker')).toHaveCount(2);
  await expect(page.getByText('5.00 km drawn')).toBeVisible();
  await clickFreeMap(page, .76, .58);
  await expect(page.locator('.map-via-marker')).toHaveCount(3);
  await page.keyboard.press('Control+z');
  await expect(page.locator('.map-via-marker')).toHaveCount(2);

  // Exercise the actual MapLibre mouse gesture, including on the mobile viewport.
  // Releasing a marker must move that point, without creating a new click point.
  const originalPoints = previewResponse.request().postDataJSON().points;
  const firstMarker = page.locator('.map-via-marker').first();
  const markerBox = await firstMarker.boundingBox();
  if (!markerBox) throw new Error('Drawn point is not visible');
  const movedPending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/routes/preview' && response.request().method() === 'POST');
  await page.mouse.move(markerBox.x + markerBox.width / 2, markerBox.y + markerBox.height / 2);
  await page.mouse.down();
  await page.mouse.move(markerBox.x + markerBox.width / 2 + 35, markerBox.y + markerBox.height / 2 + 25, { steps: 8 });
  await page.mouse.up();
  const movedResponse = await movedPending;
  expect(movedResponse.status()).toBe(200);
  const movedPoints = movedResponse.request().postDataJSON().points;
  expect(movedPoints).toHaveLength(2);
  expect(movedPoints[0]).not.toEqual(originalPoints[0]);
  expect(movedPoints[1]).toEqual(originalPoints[1]);
  await expect(page.locator('.map-via-marker')).toHaveCount(2);
  await page.keyboard.press('Control+z');
  // Undo restores the cached exact preview, without recalculating the old geometry.
  preview = await previewResponse.json();
  await expect(page.getByText('5.00 km drawn')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Find my routes' })).toBeEnabled();
  const drawnDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: /Download drawn GPX/ }).click();
  expect((await drawnDownload).suggestedFilename()).toMatch(/\.gpx$/);
  const generatedPending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/routes/generate' && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Find my routes' }).click();
  const generatedResponse = await generatedPending;
  expect(generatedResponse.status()).toBe(200);
  expect(generatedResponse.request().postDataJSON().drawnRouteId).toBe(preview.id);
  const candidates = await generatedResponse.json();
  expect(candidates.length).toBeGreaterThan(0);
  for (const candidate of candidates) expect(candidate.geometry.coordinates.slice(0, preview.geometry.coordinates.length)).toEqual(preview.geometry.coordinates);
  await expect(page.getByRole('button', { name: /Route 1/ })).toBeVisible();
  const completedDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: /^Export/ }).first().click();
  await page.getByRole('button', { name: /Download GPX/ }).click();
  const file = await completedDownload;
  const xml = await readFile((await file.path())!, 'utf8');
  expect(xml).toContain('<trkpt');
  const previewsBeforeReload = previewCalls;
  await expect(page).toHaveURL(/\/routes\/[0-9a-f-]+\?route=/);
  await page.reload();
  await expect(page.getByRole('button', { name: /Route 1/ })).toBeVisible();
  await expect(page.getByText('5.00 km drawn')).toBeVisible();
  await expect(page.locator('.map-via-marker')).toHaveCount(2);
  await page.waitForTimeout(700);
  expect(previewCalls).toBe(previewsBeforeReload);
  expect(generationCalls).toBe(1);
  const restoredDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: /Download drawn GPX/ }).click();
  expect((await restoredDownload).suggestedFilename()).toMatch(/\.gpx$/);
  expect(await page.getByText('Map unavailable', { exact: true }).count()).toBe(0);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: testInfo.outputPath('meander-drawing.png'), fullPage: true });
});

test('choose a start, generate and inspect routes, and export GPX', async ({ page }, testInfo) => {
  const errors: string[] = [];
  let generationCalls = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/routes/generate' && request.method() === 'POST') generationCalls++;
  });
  page.on('pageerror', error => errors.push(error.message));
  // No external fonts or maps are required for the deterministic E2E.
  await page.route('https://fonts.googleapis.com/**', route => route.abort());
  await page.goto('/');
  await chooseUtrecht(page);
  await expect(page.getByRole('combobox', { name: 'Start location' })).toHaveValue('Utrecht, Netherlands');
  await page.getByRole('button', { name: /Next: distance/ }).click();
  await page.getByRole('checkbox', { name: /Flexible distance/ }).check();
  await expect(page.getByText('4.25 and 5.75 km')).toBeVisible();
  await page.getByRole('button', { name: /Next: preferences/ }).click();
  await page.getByRole('checkbox', { name: /Green surroundings/ }).check();
  await page.getByRole('checkbox', { name: /Unpaved paths/ }).check();
  await expect(page.getByRole('checkbox', { name: /Avoid out-and-back/ })).toHaveCount(0);
  const generated = page.waitForResponse(response => response.url().includes('/api/routes/generate') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Find my routes' }).click();
  const generatedResponse = await generated;
  expect(generatedResponse.status()).toBe(200);
  expect(generatedResponse.request().postDataJSON().distanceFlexibilityPercent).toBe(15);
  expect(generatedResponse.request().postDataJSON().avoidBacktracking).toBe(true);
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(3);
  await expect(page.getByText('100% nature')).toHaveCount(3);
  await expect(page.getByText(/Green index/)).toHaveCount(0);
  await expect(page.getByText(/confirmed unpaved/)).toHaveCount(3);
  const second = page.getByRole('button', { name: /Route 2/ });
  await second.click();
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  const darkMapPending = page.waitForResponse(response => new URL(response.url()).pathname === '/style-dark.json');
  await page.getByRole('button', { name: 'Dark theme' }).click();
  await darkMapPending;
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(3);
  await page.waitForTimeout(500);
  await page.screenshot({ path: testInfo.outputPath('meander-routes-dark.png'), fullPage: true });
  await page.getByRole('button', { name: 'Light theme' }).click();
  await expect(page.locator('.map-marker')).toHaveCount(1);
  await expect(page.getByText('Map unavailable', { exact: true })).toHaveCount(0);
  await expect(page.locator('.maplibregl-canvas')).toBeVisible();
  await clickFreeMap(page, .94, .5);
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(3);
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  await expect(page).toHaveURL(/\/routes\/[0-9a-f-]+\?route=/);
  await page.reload();
  await expect(page.getByRole('button', { name: /Route 2/ })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: 'Change preferences' })).toContainText('green, unpaved');
  await expect(page.getByRole('button', { name: 'Change distance' })).toContainText('±15%');
  expect(generationCalls).toBe(1);
  // Wait for route animation before capturing the actual WebGL map and layout.
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(750);
  await page.screenshot({ path: testInfo.outputPath('meander.png'), fullPage: true });
  const pending = page.waitForEvent('download');
  await page.locator('.route-card.selected').getByRole('button', { name: /^Export/ }).click();
  await page.locator('.route-card.selected').getByRole('button', { name: /Download GPX/ }).click();
  const download = await pending;
  expect(download.suggestedFilename()).toMatch(/^meander-.*\.gpx$/);
  const xml = await readFile((await download.path())!, 'utf8');
  expect(xml).toContain('version="1.1"');
  expect(xml).toContain('lat="52.09"');
  expect(xml).toContain('<ele>12</ele>');
  const valid = await page.evaluate(text => {
    const doc = new DOMParser().parseFromString(text, 'application/xml');
    return doc.querySelector('parsererror') === null && doc.getElementsByTagName('trkpt').length === 4;
  }, xml);
  expect(valid).toBe(true);
  const originalUrl = page.url();
  const editedSessionPending = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/edit') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Edit route 2' }).click();
  const editedSessionResponse = await editedSessionPending;
  expect(editedSessionResponse.status()).toBe(200);
  const session = await editedSessionResponse.json();
  await expect(page.getByRole('button', { name: 'Save changes' })).toBeEnabled();
  await expect(page.locator('.map-via-marker')).toHaveCount(session.points.length);
  await expect(page.getByRole('complementary', { name: 'Route planner' })).toHaveCount(0);
  await page.locator('.map-via-marker').first().click();
  await expect(page.getByText('Point 1 selected')).toBeVisible();
  const removedPending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/routes/preview' && response.request().method() === 'POST');
  await page.keyboard.press('Delete');
  const removedResponse = await removedPending;
  expect(removedResponse.request().postDataJSON().points).toHaveLength(session.points.length);
  await expect(page.locator('.map-via-marker')).toHaveCount(session.points.length - 1);
  // A handle halfway along the line adds a point to that segment.
  await expect(page.locator('.map-ghost-marker')).toHaveCount(session.points.length);
  const insertedPending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/routes/preview' && response.request().method() === 'POST');
  await page.locator('.map-ghost-marker').nth(1).click();
  const insertedResponse = await insertedPending;
  const editedPreview = await insertedResponse.json();
  expect(insertedResponse.request().postDataJSON().points).toHaveLength(session.points.length + 1);
  await expect(page.locator('.map-via-marker')).toHaveCount(session.points.length);
  const saving = page.waitForResponse(response => new URL(response.url()).pathname === '/api/routes/save');
  await page.getByRole('button', { name: 'Save changes' }).click();
  const savedResponse = await saving;
  expect(savedResponse.status()).toBe(200);
  const editedRoutes = await savedResponse.json();
  expect(editedRoutes[0].geometry).toEqual(editedPreview.geometry);
  await expect(page).not.toHaveURL(originalUrl);
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(1);
  await page.reload();
  await expect(page.getByRole('button', { name: /Route 1/ })).toBeVisible();
  await expect(page.locator('.map-via-marker')).toHaveCount(session.points.length);
  expect(generationCalls).toBe(1);
  await page.waitForTimeout(750);
  await page.screenshot({ path: testInfo.outputPath('meander-edited.png'), fullPage: true });
  await page.goBack();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(3);
  await expect(page.getByRole('button', { name: /Route 2/ })).toHaveAttribute('aria-pressed', 'true');
  expect(errors).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('plan a long bike ride', async ({ page }) => {
  await page.route('https://fonts.googleapis.com/**', route => route.abort());
  await page.goto('/');
  await page.getByRole('radio', { name: 'Cycle' }).check();
  await chooseUtrecht(page);
  await page.getByRole('button', { name: /Next: distance/ }).click();
  await page.getByRole('button', { name: '80 km' }).click();
  await page.getByRole('button', { name: /Next: preferences/ }).click();
  const generated = page.waitForResponse(response => response.url().includes('/api/routes/generate') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Find my routes' }).click();
  const response = await generated;
  expect(response.status()).toBe(200);
  expect(response.request().postDataJSON()).toMatchObject({ activity: 'cycling', targetDistanceKm: 80 });
  await expect(page.getByRole('button', { name: /Route 1/ })).toBeVisible();
  await expect(page.getByText(/Cycle from/)).toBeVisible();
});

test('connect Garmin and send the chosen route', async ({ page, request }) => {
  // The fake server's counter spans both projects, so compare against a baseline.
  const courseCount = async () => (await (await request.get('http://127.0.0.1:5091/garmin/courses')).json()).count as number;
  const before = await courseCount();
  await page.route('https://fonts.googleapis.com/**', route => route.abort());
  await page.goto('/');
  await chooseUtrecht(page);
  await page.getByRole('button', { name: /Next: distance/ }).click();
  await page.getByRole('button', { name: /Next: preferences/ }).click();
  await page.getByRole('button', { name: 'Find my routes' }).click();
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(3);
  await page.locator('.route-card.selected').getByRole('button', { name: /^Export/ }).click();
  await page.locator('.route-card.selected').getByRole('link', { name: /Send to Garmin/ }).click();
  await expect(page.locator('.route-card.selected .route-sent')).toContainText('Sent to Garmin');
  await expect(page.locator('.route-card.selected').getByRole('link', { name: /View in Garmin Connect/ })).toHaveAttribute('target', '_blank');
  await expect(page.getByText('Garmin connected')).toBeVisible();
  expect(page.url()).not.toContain('connected=');
  expect(await courseCount()).toBe(before + 1);
  await page.reload();
  await expect(page.locator('.route-card.selected').getByRole('link', { name: /View in Garmin Connect/ })).toBeVisible();
  await page.getByRole('button', { name: 'Disconnect' }).click();
  await expect(page.locator('.route-sent')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Connect Garmin' })).toBeVisible();
});

test.describe('Dutch browser', () => {
  test.use({ locale: 'nl-NL' });
  test('shows the planner in Dutch and switches to English', async ({ page }) => {
    await page.route('https://fonts.googleapis.com/**', route => route.abort());
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Waar begin je?' })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'nl');
    await chooseUtrecht(page, 'Startlocatie');
    await page.getByRole('button', { name: /Volgende: afstand/ }).click();
    await page.getByRole('button', { name: /Volgende: voorkeuren/ }).click();
    await page.getByRole('checkbox', { name: /Groene omgeving/ }).check();
    await page.getByRole('button', { name: 'Vind mijn routes' }).click();
    await expect(page.getByText(/% natuur/).first()).toBeVisible();
    await page.getByRole('button', { name: 'EN', exact: true }).click();
    await expect(page.getByText(/% nature/).first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'EN', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  });
});
