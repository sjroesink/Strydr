import type { StyleSpecification } from 'maplibre-gl';
import { preserveRouteOverlays } from './mapTheme';

it('keeps route data and overlays while swapping the basemap', () => {
  const previous: StyleSpecification = { version: 8,
    sources: { routes: { type: 'geojson', data: { type: 'FeatureCollection', features: [
      { type: 'Feature', properties: { id: 'selected', active: true }, geometry: { type: 'LineString', coordinates: [[5, 52], [5.01, 52.01]] } },
    ] } } },
    layers: [{ id: 'route-lines', type: 'line', source: 'routes', paint: { 'line-color': '#d3733c' } }],
  };
  const next: StyleSpecification = { version: 8, sources: {}, layers: [{ id: 'background', type: 'background' }] };
  const result = preserveRouteOverlays(previous, next, 'dark');
  expect(result.sources.routes).toBe(previous.sources.routes);
  expect(result.layers.map(layer => layer.id)).toEqual(['background', 'route-lines']);
  expect(result.layers[1]).toEqual(previous.layers[0]);
});
