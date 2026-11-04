import type { StyleSpecification, VectorSourceSpecification } from 'maplibre-gl';
import { resolveMapStyle } from './mapStyle';

const base = 'http://127.0.0.1:5173/routes/abc';

it('prefers an explicit style URL and its dark counterpart', () => {
  const env = { VITE_MAP_STYLE_URL: 'https://tiles/light.json', VITE_MAP_STYLE_DARK_URL: 'https://tiles/dark.json',
    VITE_PMTILES_URL: '/map/nl.pmtiles', VITE_MAPTILER_KEY: 'key' };
  expect(resolveMapStyle(env, 'light', 'nl', base)).toMatchObject({ style: 'https://tiles/light.json', provider: 'style', configured: true });
  expect(resolveMapStyle(env, 'dark', 'nl', base).style).toBe('https://tiles/dark.json');
  expect(resolveMapStyle({ VITE_MAP_STYLE_URL: 'https://tiles/light.json' }, 'dark', 'nl', base).style).toBe('https://tiles/light.json');
});

it('builds a keyless local PMTiles style with absolute tile, glyph and sprite URLs', () => {
  const result = resolveMapStyle({ VITE_PMTILES_URL: '/map/netherlands.pmtiles', VITE_MAPTILER_KEY: 'key' }, 'dark', 'nl', base);
  expect(result).toMatchObject({ provider: 'pmtiles', configured: true });
  const style = result.style as StyleSpecification;
  expect((style.sources.protomaps as VectorSourceSpecification).url).toBe('pmtiles://http://127.0.0.1:5173/map/netherlands.pmtiles');
  expect(style.glyphs).toBe('http://127.0.0.1:5173/map/assets/fonts/{fontstack}/{range}.pbf');
  expect(style.sprite).toBe('http://127.0.0.1:5173/map/assets/sprites/v4/dark');
  expect(style.layers.length).toBeGreaterThan(10);
  expect(style.layers.every(layer => layer.type === 'background' || ('source' in layer && layer.source === 'protomaps'))).toBe(true);
  const light = resolveMapStyle({ VITE_PMTILES_URL: '/map/netherlands.pmtiles', VITE_MAP_ASSETS_URL: 'https://cdn.example/assets/' }, 'light', 'nl', base);
  expect((light.style as StyleSpecification).sprite).toBe('https://cdn.example/assets/sprites/v4/light');
});

it('falls back to MapTiler, then to an unconfigured background', () => {
  expect(resolveMapStyle({ VITE_MAPTILER_KEY: 'a b' }, 'dark', 'nl', base)).toEqual({
    style: 'https://api.maptiler.com/maps/base-v4-dark/style.json?key=a%20b', provider: 'maptiler', configured: true });
  const none = resolveMapStyle({ VITE_MAPTILER_KEY: 'your-key' }, 'light', 'nl', base);
  expect(none).toMatchObject({ provider: 'none', configured: false });
  expect(typeof none.style).toBe('object');
});

it('protomaps labels follow the UI language', () => {
  const english = resolveMapStyle({ VITE_PMTILES_URL: '/map/netherlands.pmtiles' }, 'light', 'en', base).style as StyleSpecification;
  const dutch = resolveMapStyle({ VITE_PMTILES_URL: '/map/netherlands.pmtiles' }, 'light', 'nl', base).style as StyleSpecification;
  expect(JSON.stringify(english.layers)).toContain('name:en');
  expect(JSON.stringify(dutch.layers)).toContain('name:nl');
});
