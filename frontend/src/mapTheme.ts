import type { LayerSpecification, StyleSpecification } from 'maplibre-gl';
import type { ColorScheme } from './useTheme';

const overlaySources = new Set(['routes', 'drawing', 'radius']);
export function themeOverlay(layer: LayerSpecification, scheme: ColorScheme): LayerSpecification {
  const dark = scheme === 'dark';
  if (layer.type === 'line') {
    if (layer.id === 'route-halo' || layer.id === 'drawing-halo')
      return { ...layer, paint: { ...layer.paint, 'line-color': dark ? '#181818' : '#ffffff' } };
    if (layer.id === 'drawing-line' || layer.id === 'radius-line')
      return { ...layer, paint: { ...layer.paint, 'line-color': dark ? '#ec9b63' : '#215c45' } };
  }
  if (dark && layer.id === 'radius-fill' && layer.type === 'fill')
    return { ...layer, paint: { ...layer.paint, 'fill-color': '#ec9b63' } };
  return layer;
}

export function preserveRouteOverlays(previous: StyleSpecification | undefined, next: StyleSpecification, scheme: ColorScheme): StyleSpecification {
  const base: StyleSpecification = scheme === 'dark' ? { ...next, layers: next.layers.map(layer => {
    if (layer.type === 'background') return { ...layer, paint: { ...layer.paint, 'background-color': '#1b1b1b' } };
    if (layer.type === 'fill' && /landcover|landuse|wood|forest|park|grass|scrub|farmland/i.test(layer.id) && !/water|building/i.test(layer.id))
      return { ...layer, paint: { ...layer.paint, 'fill-color': '#2d2b25' } };
    return layer;
  }) } : next;
  if (!previous) return base;
  const layers = previous.layers.filter(layer => 'source' in layer && overlaySources.has(layer.source as string));
  const ids = new Set(layers.map(layer => layer.id));
  return { ...base,
    sources: { ...base.sources, ...Object.fromEntries(Object.entries(previous.sources).filter(([id]) => overlaySources.has(id))) },
    layers: [...base.layers.filter(layer => !ids.has(layer.id)), ...layers.map(layer => themeOverlay(layer, scheme))],
  };
}
