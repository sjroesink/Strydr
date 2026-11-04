import type { LayerSpecification, StyleSpecification } from 'maplibre-gl';
import { layers, namedFlavor } from '@protomaps/basemaps';
import type { ColorScheme } from './useTheme';
import type { Language } from './i18n';

export type MapEnv = {
  VITE_MAP_STYLE_URL?: string; VITE_MAP_STYLE_DARK_URL?: string;
  VITE_PMTILES_URL?: string; VITE_MAP_ASSETS_URL?: string;
  VITE_MAPTILER_KEY?: string;
};
export type MapProvider = 'style' | 'pmtiles' | 'maptiler' | 'none';
export type ResolvedMapStyle = { style: string | StyleSpecification; provider: MapProvider; configured: boolean };

const fallbackStyle: StyleSpecification = { version: 8, sources: {}, layers: [{ id: 'land', type: 'background', paint: { 'background-color': '#e9ede2' } }] };
const darkFallbackStyle: StyleSpecification = { version: 8, sources: {}, layers: [{ id: 'land', type: 'background', paint: { 'background-color': '#1b1b1b' } }] };

const absolute = (url: string, base: string) => new URL(url, base).href.replace(/\/$/, '');

// A local Protomaps archive needs no key: tiles, glyphs and sprites are all served by our own origin.
function pmtilesStyle(archive: string, assets: string, scheme: ColorScheme, language: Language, base: string): StyleSpecification {
  const flavor = scheme === 'dark' ? 'dark' : 'light';
  const assetsUrl = absolute(assets, base);
  return {
    version: 8,
    glyphs: `${assetsUrl}/fonts/{fontstack}/{range}.pbf`,
    sprite: `${assetsUrl}/sprites/v4/${flavor}`,
    sources: { protomaps: { type: 'vector', url: `pmtiles://${absolute(archive, base)}`,
      attribution: '<a href="https://protomaps.com">Protomaps</a> © <a href="https://openstreetmap.org/copyright">OpenStreetMap</a>' } },
    layers: layers('protomaps', namedFlavor(flavor), { lang: language }) as LayerSpecification[],
  };
}

// Priority: explicit style URL, local PMTiles, MapTiler key, plain background.
export function resolveMapStyle(env: MapEnv, scheme: ColorScheme, language: Language = 'nl',
  base = globalThis.location?.href ?? 'http://localhost/'): ResolvedMapStyle {
  const dark = scheme === 'dark';
  if (env.VITE_MAP_STYLE_URL)
    return { style: dark ? env.VITE_MAP_STYLE_DARK_URL || env.VITE_MAP_STYLE_URL : env.VITE_MAP_STYLE_URL, provider: 'style', configured: true };
  if (env.VITE_PMTILES_URL)
    return { style: pmtilesStyle(env.VITE_PMTILES_URL, env.VITE_MAP_ASSETS_URL || '/map/assets', scheme, language, base), provider: 'pmtiles', configured: true };
  const key = env.VITE_MAPTILER_KEY;
  if (key && !key.startsWith('your-'))
    return { style: `https://api.maptiler.com/maps/${dark ? 'base-v4-dark' : 'base-v4'}/style.json?key=${encodeURIComponent(key)}`, provider: 'maptiler', configured: true };
  return { style: dark ? darkFallbackStyle : fallbackStyle, provider: 'none', configured: false };
}
