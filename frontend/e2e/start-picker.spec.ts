import { expect, test } from '@playwright/test';

for (const language of ['nl', 'en']) for (const theme of ['light', 'dark']) {
  test(`${language} ${theme}: start map selection steps the sheet down and restores it on cancel or pick`, async ({ page }, info) => {
    await page.addInitScript(({ language, theme }) => {
      localStorage.setItem('meander.language', language); localStorage.setItem('meander.theme', theme);
    }, { language, theme });
    await page.route('https://fonts.googleapis.com/**', route => route.abort());
    await page.goto('/');
    await expect(page.locator('.maplibregl-canvas')).toBeVisible();
    await expect(page.locator('.map-panel')).toHaveAttribute('aria-busy', 'false');
    const viewport = page.viewportSize()!;
    await page.mouse.click(viewport.width * .75, viewport.height * .25);
    const input = page.getByRole('combobox').first();
    await expect(input).toHaveValue(/\d+\.\d+, /);
    const selected = await input.inputValue();
    const pick = page.getByRole('button', { name: language === 'nl' ? 'Kies op kaart' : 'Pick on map', exact: true });
    const cancel = page.getByRole('button', { name: language === 'nl' ? 'Start kiezen annuleren' : 'Cancel start selection' });
    const next = page.getByRole('button', { name: language === 'nl' ? 'Volgende: afstand' : 'Next: distance', exact: true });
    await pick.click(); await expect(cancel).toBeFocused();
    await expect(next).toHaveCount(0);
    await page.getByRole('form').evaluate((form: HTMLFormElement) => form.requestSubmit());
    await expect(page.locator('.app-shell')).toHaveClass(/step-start/);
    if (info.project.name === 'mobile') {
      await expect(page.locator('.app-shell')).toHaveClass(/sheet-collapsed/);
      await expect.poll(async () => (await page.locator('.planner-sheet').boundingBox())!.height).toBeLessThanOrEqual(140);
    } else await expect(page.locator('.app-shell')).not.toHaveClass(/sheet-collapsed/);
    await page.screenshot({ path: info.outputPath(`start-picker-${language}-${theme}.png`), fullPage: true });
    await cancel.click();
    await expect(input).toHaveValue(selected); await expect(pick).toBeFocused();
    await expect(next).toBeVisible(); await expect(page.locator('.app-shell')).not.toHaveClass(/sheet-collapsed/);
    await pick.click();
    await page.mouse.click(viewport.width * .65, viewport.height * .3);
    await expect(cancel).toHaveCount(0);
    await expect(next).toBeVisible(); await expect(pick).toBeFocused();
    await expect(input).not.toHaveValue(selected);
    await expect(page.locator('.app-shell')).not.toHaveClass(/sheet-collapsed/);
    await expect(page.locator('html')).toHaveAttribute('lang', language);
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
  });
}
