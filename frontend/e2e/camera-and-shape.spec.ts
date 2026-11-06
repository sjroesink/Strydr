import { expect, test } from '@playwright/test';
import { candidate } from '../src/test/fixtures';

const request = { start: { latitude: 52.09, longitude: 5.12 }, end: null, radiusKm: null, targetDistanceKm: 12, activity: 'walking', candidateCount: 3, preferGreen: false, preferUnpaved: false, avoidBacktracking: true };
const routes = [
  [[5.12, 52.09], [5.3, 52.2], [5, 52.15], [5.12, 52.09]],
  [[5.12, 52.09], [5.9, 52.5], [5.3, 52.8], [5.12, 52.09]],
  [[5.12, 52.09], [4.6, 51.8], [5.1, 51.7], [5.12, 52.09]],
].map((coordinates, index) => ({ ...candidate(index + 1), searchId: 'camera-shape', geometry: { type: 'LineString' as const, coordinates } }));
async function fixture(page: import('@playwright/test').Page) {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/searches/camera-shape') await route.fulfill({ json: { id: 'camera-shape', request, candidates: routes, drawnPoints: [] } });
    else if (path === '/api/connections' || path === '/api/routes/sent') await route.fulfill({ json: [] });
    else await route.abort(); // No production routing, storage or Garmin writes.
  });
}
function contrast(a: string, b: string) {
  const lum = (color: string) => color.match(/\d+/g)!.slice(0, 3).map(value => { const c = Number(value) / 255; return c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; }).reduce((sum, c, i) => sum + c * [.2126, .7152, .0722][i], 0);
  return (Math.max(lum(a), lum(b)) + .05) / (Math.min(lum(a), lum(b)) + .05);
}
for (const language of ['NL', 'EN']) for (const theme of ['light', 'dark']) {
  test(`${language} ${theme}: native shape cards and endpoint picker expose the map without a search action`, async ({ page }, info) => {
    await fixture(page); await page.goto('/routes/camera-shape');
    await page.getByRole('button', { name: language, exact: true }).click();
    await page.evaluate(theme => { document.documentElement.dataset.theme = theme; }, theme);
    await page.getByRole('button', { name: language === 'NL' ? 'Voorkeuren wijzigen' : 'Change preferences', exact: true }).click();
    await expect(page.locator('.step-title')).toBeFocused();
    const endpoint = page.getByRole('checkbox', { name: language === 'NL' ? 'Ergens anders eindigen' : 'Finish somewhere else' });
    await endpoint.focus(); await page.keyboard.press('Space'); await expect(endpoint).toBeChecked();
    const label = endpoint.locator('..');
    const style = await label.evaluate(element => { const s = getComputedStyle(element); const surface = getComputedStyle(document.querySelector('.planner-sheet')!); return { color: s.color, fill: s.backgroundColor, border: s.borderTopColor, surface: surface.backgroundColor, outline: s.outlineStyle, check: getComputedStyle(element.querySelector('.card-check')!).display }; });
    expect(contrast(style.color, style.fill)).toBeGreaterThanOrEqual(4.5);
    expect(contrast(style.border, style.surface)).toBeGreaterThanOrEqual(3);
    expect(style.outline).toBe('solid'); expect(style.check).toBe('grid');
    await page.screenshot({ path: info.outputPath('shape-checkboxes.png'), fullPage: true });
    const picker = page.getByRole('button', { name: language === 'NL' ? 'Kies eindpunt op kaart' : 'Pick finish on map', exact: true });
    const search = page.getByRole('button', { name: language === 'NL' ? 'Vind mijn routes' : 'Find my routes', exact: true });
    await picker.click();
    await expect(search).toHaveCount(0);
    if (info.project.name === 'mobile') await expect(page.locator('.app-shell')).toHaveClass(/sheet-collapsed/);
    else await expect(page.locator('.app-shell')).not.toHaveClass(/sheet-collapsed/);
    await page.getByRole('form').evaluate(form => (form as HTMLFormElement).requestSubmit());
    await expect(search).toHaveCount(0);
    await page.screenshot({ path: info.outputPath('endpoint-picker.png'), fullPage: true });
    const canvas = page.locator('.maplibregl-canvas'); const box = await canvas.boundingBox();
    await page.mouse.click(box!.x + box!.width * .72, box!.y + box!.height * .38);
    await expect(search).toBeVisible();
    const input = page.getByRole('combobox', { name: language === 'NL' ? 'Eindlocatie' : 'End location' });
    const selected = await input.inputValue(); expect(selected).toMatch(/\d+\.\d+, /);
    await picker.click();
    const cancel = page.getByRole('button', { name: language === 'NL' ? 'Eindpunt kiezen annuleren' : 'Cancel finish selection' });
    await expect(cancel).toBeFocused(); await cancel.click();
    await expect(input).toHaveValue(selected); await expect(search).toBeVisible(); await expect(picker).toBeFocused();
    const via = page.getByRole('checkbox', { name: language === 'NL' ? 'Langs een plek gaan' : 'Pass through a spot' });
    await via.focus(); await page.keyboard.press('Space'); await expect(via).toBeChecked();
    await expect(page.locator('.map-instruction')).toBeVisible();
    await page.keyboard.press('Space'); await expect(via).not.toBeChecked();
  });
}

test('actual MapLibre fits each selected route completely beside the sheet, without resetting on theme or pan', async ({ page }, info) => {
  await fixture(page); await page.goto('/');
  await page.locator('.maplibregl-canvas').waitFor();
  await page.evaluate(async () => {
    const url = performance.getEntriesByType('resource').map(e => e.name).find(name => /\/maplibre-gl\.js\?/.test(name));
    if (!url) throw new Error('MapLibre module not found');
    const module = await import(/* @vite-ignore */ url);
    const original = module.Map.prototype.fitBounds;
    (window as any).__cameraFits = [];
    module.Map.prototype.fitBounds = function(bounds: any, options: any) {
      (window as any).__camera = this; (window as any).__cameraFits.push(bounds.toArray());
      return original.call(this, bounds, options);
    };
    history.pushState(null, '', '/routes/camera-shape'); dispatchEvent(new PopStateEvent('popstate'));
  });
  await expect.poll(() => page.evaluate(() => (window as any).__cameraFits.length)).toBe(1);
  for (let index = 0; index < routes.length; index++) {
    await page.locator('.route-select').nth(index).click();
    await expect(page.locator('.route-select').nth(index)).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(() => page.evaluate(() => (window as any).__cameraFits.length)).toBe(index + 1);

    await expect.poll(() => page.evaluate(coords => {
      const map = (window as any).__camera;
      if (map.isMoving()) return false;
      const p = map.getPadding(); const canvas = map.getCanvas();
      return coords.every(point => { const q = map.project(point); return q.x >= p.left - 1 && q.x <= canvas.clientWidth - p.right + 1 && q.y >= p.top - 1 && q.y <= canvas.clientHeight - p.bottom + 1; });
    }, routes[index].geometry.coordinates)).toBe(true);
    await page.screenshot({ path: info.outputPath(`selected-route-${index + 1}.png`), fullPage: true });
  }
  await page.evaluate(() => { (window as any).__camera.panBy([30, 20], { duration: 0 }); });
  const fits = await page.evaluate(() => (window as any).__cameraFits.length);
  await page.getByRole('button', { name: 'Dark theme', exact: true }).click();
  expect(await page.evaluate(() => (window as any).__cameraFits.length)).toBe(fits);
});
