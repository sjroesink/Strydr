// Regenerate with: node scripts/generate-icons.mjs [Chromium executable]
// Uses the existing favicon's path; maskable artwork stays inside the 80% safe circle.
import { chromium } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
const browser = await chromium.launch({ executablePath: process.argv[2] || undefined, headless: true });
try {
  const page = await browser.newPage();
  await mkdir('public/icons', { recursive: true });
  for (const [size, maskable, file] of [
    [192, false, 'icons/icon-192'], [512, false, 'icons/icon-512'],
    [192, true, 'icons/maskable-192'], [512, true, 'icons/maskable-512'], [180, false, 'apple-touch-icon'],
  ]) {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 64 64"><rect width="64" height="64" fill="#215c45"/><g transform="${maskable ? 'translate(8 8) scale(.75)' : 'translate(3.2 3.2) scale(.9)'}"><path d="M14 45V24q0-12 10-7l7 10 7-10q12-5 12 7v21" fill="none" stroke="#f7f5ed" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/></g></svg>`;
    const png = await page.evaluate(async ({ svg, size }) => {
      const img = new Image(); img.src = `data:image/svg+xml,${encodeURIComponent(svg)}`; await img.decode();
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = size;
      const context = canvas.getContext('2d'); context.drawImage(img, 0, 0);
      const pixels = context.getImageData(0, 0, size, size).data;
      if ([...pixels].some((v, i) => i % 4 === 3 && v !== 255)) throw new Error('Icon must be opaque');
      return canvas.toDataURL('image/png').split(',')[1];
    }, { svg, size });
    await writeFile(`public/${file}.png`, Buffer.from(png, 'base64'));
    if (size === 512) await writeFile(`public/${maskable ? 'icons/maskable' : 'icons/icon'}.svg`, svg + '\n');
    console.log(`${file}.png: ${size}x${size}, fully opaque`);
  }
} finally { await browser.close(); }
