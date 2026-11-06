import { expect, test } from '@playwright/test';
import { candidate } from '../src/test/fixtures';
import type { RouteRequest } from '../src/types';

for (const language of ['NL', 'EN']) {
  test(`${language}: saved results return to the wizard with all search settings intact`, async ({ page }) => {
    const request: RouteRequest = { start: { latitude: 52.09, longitude: 5.12 }, end: null,
      radiusKm: 3, targetDistanceKm: 12, activity: 'walking', candidateCount: 3,
      preferGreen: true, preferUnpaved: true, distanceFlexibilityPercent: 20, avoidBacktracking: true };
    const candidates = [1, 2, 3].map(n => ({ ...candidate(n), searchId: 'navigation-fixture', expiresAt: null }));
    const generated: RouteRequest[] = [];
    // Browser fixtures only: no production routing, storage, or connection mutations.
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/api/searches/navigation-fixture') {
        await route.fulfill({ json: { id: 'navigation-fixture', createdAt: '2026-11-06T00:00:00Z', request, candidates, drawnPoints: [] } });
      } else if (path === '/api/routes/generate') {
        generated.push(route.request().postDataJSON());
        await route.fulfill({ json: candidates });
      } else if (path === '/api/connections' || path === '/api/routes/sent') {
        await route.fulfill({ json: [] });
      } else { await route.abort(); }
    });
    await page.goto('/routes/navigation-fixture');
    await expect(page.locator('.route-card')).toHaveCount(3);
    await page.getByRole('button', { name: language, exact: true }).click();
    await page.reload();
    const adjust = page.getByRole('button', { name: language === 'NL' ? 'Zoekopdracht aanpassen' : 'Adjust search', exact: true });
    await expect(adjust).toBeVisible();
    await adjust.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('input[name="activity"][value="walking"]')).toBeChecked();
    await expect(page.locator('.location-search input').first()).toHaveValue('52.09000, 5.12000');
    const back = page.getByRole('button', { name: language === 'NL' ? 'Terug naar routes' : 'Back to routes', exact: true });
    await back.click();
    await expect(page.locator('.route-card')).toHaveCount(3);
    await adjust.click();
    await page.locator('.generate-button').click();
    await expect(page.locator('#distance')).toHaveValue('12');
    await expect(page.locator('#distance-margin')).toHaveValue('20');
    await page.locator('.generate-button').click();
    await page.locator('.generate-button').click();
    await expect(page.locator('.route-card')).toHaveCount(3);
    expect(generated).toHaveLength(1);
    expect(generated[0]).toMatchObject(request);
  });
}
