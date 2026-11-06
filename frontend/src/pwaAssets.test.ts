import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
const publicFile = (name: string) => readFileSync(`public/${name}`);
describe('installable Meander identity', () => {
  it('ships a standalone manifest with opaque, correctly sized normal and safe-zone maskable icons', () => {
    const manifest = JSON.parse(publicFile('manifest.webmanifest').toString());
    expect(manifest).toMatchObject({ name: 'Meander', short_name: 'Meander', id: '/', start_url: '/', scope: '/', display: 'standalone', theme_color: '#215c45', background_color: '#f7f5ed' });
    for (const size of [192, 512]) for (const purpose of ['any', 'maskable']) {
      const icon = manifest.icons.find((i: { sizes: string; purpose: string }) => i.sizes === `${size}x${size}` && i.purpose === purpose);
      expect(icon).toBeDefined();
      const png = publicFile(icon.src.replace(/^\//, ''));
      expect(png.subarray(1, 4).toString()).toBe('PNG');
      expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([size, size]);
    }
    expect(publicFile('apple-touch-icon.png').readUInt32BE(16)).toBe(180);
    const html = readFileSync('index.html', 'utf8');
    expect(html).toContain('rel="manifest"');
    expect(html).toContain('rel="apple-touch-icon"');
  });
});
