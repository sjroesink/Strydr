import { expect, test } from '@playwright/test';

for (const language of ['nl', 'en']) for (const theme of ['light', 'dark']) {
  test(`${language} ${theme}: compact options group preferences and installation without changing the planner`, async ({ page }, info) => {
    await page.addInitScript(({ language, theme }) => {
      localStorage.setItem('meander.language', language); localStorage.setItem('meander.theme', theme);
    }, { language, theme });
    await page.route('https://fonts.googleapis.com/**', route => route.abort());
    await page.goto('/');
    const toggle = page.getByRole('button', { name: language === 'nl' ? 'Meander instellingen' : 'Meander options' });
    const installName = language === 'nl' ? 'Meander installeren' : 'Install Meander';
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByRole('button', { name: 'NL', exact: true })).toHaveCount(0);
    // Fixture browser event proves UI placement only; production PWA tests check real installability.
    await page.evaluate(() => window.dispatchEvent(Object.assign(new Event('beforeinstallprompt', { cancelable: true }), {
      prompt: async () => {}, userChoice: Promise.resolve({ outcome: 'dismissed' }),
    })));
    await expect(page.getByRole('button', { name: installName })).toHaveCount(0);
    await toggle.focus(); await page.keyboard.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'NL', exact: true })).toBeFocused();
    await expect(page.getByRole('button', { name: installName })).toBeVisible();
    await expect(page.getByRole('button', { name: language.toUpperCase(), exact: true })).toHaveAttribute('aria-pressed', 'true');
    const panel = await page.locator('.settings-panel').boundingBox();
    expect(panel).not.toBeNull();
    expect(panel!.x).toBeGreaterThanOrEqual(0);
    expect(panel!.x + panel!.width).toBeLessThanOrEqual(page.viewportSize()!.width);
    expect(panel!.y + panel!.height).toBeLessThanOrEqual(page.viewportSize()!.height);
    const target = await toggle.boundingBox();
    expect(target!.width).toBeGreaterThanOrEqual(44); expect(target!.height).toBeGreaterThanOrEqual(44);
    await page.screenshot({ path: info.outputPath(`options-${language}-${theme}.png`), fullPage: true });
    await page.keyboard.press('Escape'); await expect(toggle).toBeFocused();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await page.screenshot({ path: info.outputPath(`header-${language}-${theme}.png`), fullPage: true });
    await toggle.click();
    // Use an actually uncovered point; the desktop popover intentionally overlays the sheet heading.
    await page.mouse.click(page.viewportSize()!.width - 12, 60);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await expect(page.getByRole('radio', { name: language === 'nl' ? 'Hardlopen' : 'Run', exact: true })).toBeChecked();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}
