import { expect, test } from '@playwright/test';

function luminance(color: string) {
  const [r, g, b] = color.match(/\d+/g)!.slice(0, 3).map(value => {
    const channel = Number(value) / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function contrast(a: string, b: string) {
  const [low, high] = [luminance(a), luminance(b)].sort((x, y) => x - y);
  return (high + 0.05) / (low + 0.05);
}

for (const theme of ['light', 'dark']) {
  for (const language of ['NL', 'EN']) {
    test(`${theme} ${language}: activity selection is distinct and keyboard accessible`, async ({ page }) => {
      await page.goto('/');
      await page.locator('.activity-picker').waitFor();
      await page.evaluate(value => { document.documentElement.dataset.theme = value; }, theme);
      await page.getByRole('button', { name: /Meander options|Meander instellingen/ }).click();
      await page.getByRole('button', { name: language, exact: true }).click();
      await page.keyboard.press('Escape');
      for (const activity of ['running', 'walking', 'cycling']) {
        const radio = page.locator(`input[name="activity"][value="${activity}"]`);
        const label = radio.locator('..');
        await label.click();
        await expect(radio).toBeChecked();
        await expect(page.locator('input[name="activity"]:checked')).toHaveCount(1);
        const style = await label.evaluate(element => {
          const selected = getComputedStyle(element);
          const group = getComputedStyle(element.parentElement!);
          return { text: selected.color, fill: selected.backgroundColor,
            surrounding: group.backgroundColor, border: selected.borderTopColor,
            borderWidth: selected.borderTopWidth, shadow: selected.boxShadow };
        });
        expect(contrast(style.text, style.fill), 'selected text contrast').toBeGreaterThanOrEqual(4.5);
        expect(contrast(style.fill, style.surrounding), 'selected boundary contrast').toBeGreaterThanOrEqual(3);
        expect(parseFloat(style.borderWidth), 'visible selected border').toBeGreaterThanOrEqual(1);
        expect(contrast(style.border, style.surrounding)).toBeGreaterThanOrEqual(3);
        expect(style.shadow, 'inset outline distinguishes selection without color alone').toContain('inset');
        await radio.focus();
        await page.keyboard.press('ArrowRight');
        await expect(radio).not.toBeChecked();
        await expect(page.locator('input[name="activity"]:checked')).toHaveCount(1);
        const focusOutline = await page.locator('input[name="activity"]:checked').locator('..')
          .evaluate(element => getComputedStyle(element).outlineStyle);
        expect(focusOutline).toBe('solid');
      }
    });
  }
}
