import { useEffect, useMemo, useRef, useState } from 'react';
import * as maplibregl from 'maplibre-gl';
import type { GeoJSONSource, StyleSpecification } from 'maplibre-gl';
import { Protocol } from 'pmtiles';
import 'maplibre-gl/dist/maplibre-gl.css';
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url';
import type { FeatureCollection } from 'geojson';
import type { Location, RouteCandidate } from '../types';
import type { ColorScheme } from '../useTheme';
import { preserveRouteOverlays, themeOverlay } from '../mapTheme';
import { resolveMapStyle, type MapEnv } from '../mapStyle';
import { segmentMidpoints } from '../routeEditing';
import { useI18n, type Language, type MessageKey } from '../i18n';

export const ROUTE_COLORS = ['#d3733c', '#466bc3', '#9363a9'];
export type MapMarkerTarget = 'start' | 'end' | number;
type Props = {
  colorScheme: ColorScheme; language: Language;
  start: Location | null; end: Location | null; radiusKm: number | null;
  candidates: RouteCandidate[]; selectedId: string | null; highlightedId: string | null;
  drawnPoints: Location[]; preview: RouteCandidate | null;
  previousPreview: RouteCandidate | null;
  mode: 'browse' | 'start' | 'end' | 'draw'; onPick: (location: Location) => void; onSelect: (id: string) => void;
  onMoveMarker: (target: MapMarkerTarget, location: Location) => void;
  onMarkerDragChange: (dragging: boolean) => void;
  selectedPoint: number | null; onSelectPoint: (index: number | null) => void;
  // Editing a complete route shows a handle halfway along every segment.
  editing: boolean; onInsertPoint: (index: number, location: Location) => void;
  // Screen space covered by the planner, so the camera keeps routes visible beside it.
  padding: MapPadding; fitRequest: number;
};
export type MapPadding = { top: number; right: number; bottom: number; left: number };
// On touch, a dragged point rides this far above the finger so it stays visible.
const TOUCH_LIFT_PX = 44;

const empty: FeatureCollection = { type: 'FeatureCollection', features: [] };
// MapLibre 6 ships a separate ESM worker. Let Vite bundle its shared imports
// rather than resolving it relative to an optimized or hashed main chunk.
maplibregl.setWorkerUrl(workerUrl);
// Local basemaps are single PMTiles archives read with HTTP range requests.
maplibregl.addProtocol('pmtiles', new Protocol().tile);

// MapLibre keeps the grab offset for the whole gesture. Shifting it once the drag
// starts moves the point above the finger, so you can see where it will land.
// A chevron pointing along the line, drawn once and repeated by symbol layers so
// the route shows which way it runs. Re-added whenever a basemap swap drops it.
const ARROW_IMAGE = 'route-arrow';
function addArrowImage(map: maplibregl.Map) {
  if (map.hasImage(ARROW_IMAGE)) return;
  const ratio = 2, size = 18 * ratio;
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const context = canvas.getContext('2d');
  if (!context) return;
  context.lineCap = 'round'; context.lineJoin = 'round';
  const chevron = () => { context.beginPath(); context.moveTo(12, 9); context.lineTo(24, 18); context.lineTo(12, 27); context.stroke(); };
  context.strokeStyle = 'rgba(0, 0, 0, .6)'; context.lineWidth = 10; chevron();
  context.strokeStyle = '#ffffff'; context.lineWidth = 5; chevron();
  map.addImage(ARROW_IMAGE, context.getImageData(0, 0, size, size), { pixelRatio: ratio });
}
const arrowLayout = { 'symbol-placement': 'line', 'symbol-spacing': 90, 'icon-image': ARROW_IMAGE,
  'icon-allow-overlap': true, 'icon-ignore-placement': true, 'icon-rotation-alignment': 'map', 'icon-keep-upright': false } as const;

function liftAboveFinger(marker: maplibregl.Marker) {
  const internal = marker as unknown as { _positionDelta?: { add: (p: { x: number; y: number }) => unknown } | null };
  if (internal._positionDelta?.add) internal._positionDelta = internal._positionDelta.add({ x: 0, y: TOUCH_LIFT_PX }) as typeof internal._positionDelta;
}

export function radiusRing(center: Location, radiusKm: number): number[][] {
  const lat = center.latitude * Math.PI / 180;
  const lng = center.longitude * Math.PI / 180;
  const angular = radiusKm / 6371.0088;
  return Array.from({ length: 97 }, (_, index) => {
    const bearing = index / 96 * Math.PI * 2;
    const nextLat = Math.asin(Math.sin(lat) * Math.cos(angular) + Math.cos(lat) * Math.sin(angular) * Math.cos(bearing));
    const nextLng = lng + Math.atan2(Math.sin(bearing) * Math.sin(angular) * Math.cos(lat), Math.cos(angular) - Math.sin(lat) * Math.sin(nextLat));
    return [nextLng * 180 / Math.PI, nextLat * 180 / Math.PI];
  });
}

export default function RouteMap(props: Props) {
  const { t } = useI18n();
  const container = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const latest = useRef(props);
  latest.current = props;
  const markers = useRef(new Map<string, maplibregl.Marker>());
  const draggingMarker = useRef<string | null>(null);
  const ignoreMapClickUntil = useRef(0);
  const suppressFitAfterDrag = useRef(false);
  const ghosts = useRef<maplibregl.Marker[]>([]);
  const [dragging, setDragging] = useState(false);
  const [ready, setReady] = useState(0);
  const [error, setError] = useState<MessageKey | null>(null);
  const [retry, setRetry] = useState(0);
  // Memoized so a generated PMTiles style keeps its identity and is only swapped on theme changes.
  const { style: selectedStyle, configured } = useMemo(() => resolveMapStyle(import.meta.env as MapEnv, props.colorScheme, props.language), [props.colorScheme, props.language]);
  const appliedStyle = useRef<string | StyleSpecification | null>(null);

  useEffect(() => {
    if (!container.current) return;
    setError(null); setReady(0);
    let map: maplibregl.Map;
    try {
      map = new maplibregl.Map({
        container: container.current,
        style: selectedStyle,
        transformStyle: (previous, next) => preserveRouteOverlays(previous, next, latest.current.colorScheme),
        center: [5.3, 52.1], zoom: 7, attributionControl: { compact: true },
      });
    } catch {
      setError('map.noWebgl');
      return;
    }
    mapRef.current = map;
    appliedStyle.current = selectedStyle;
    map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'bottom-right');
    map.on('error', () => setError('map.tilesFailed'));
    map.on('styleimagemissing', event => { if (event.id === ARROW_IMAGE) addArrowImage(map); });
    map.on('load', () => {
      addArrowImage(map);
      map.addSource('radius', { type: 'geojson', data: empty });
      map.addLayer({ id: 'radius-fill', type: 'fill', source: 'radius', paint: { 'fill-color': '#215c45', 'fill-opacity': .06 } });
      map.addLayer({ id: 'radius-line', type: 'line', source: 'radius', paint: { 'line-color': '#215c45', 'line-width': 1.5, 'line-dasharray': [3, 3] } });
      map.addSource('drawing', { type: 'geojson', data: empty });
      map.addLayer({ id: 'drawing-halo', type: 'line', source: 'drawing', layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': '#ffffff', 'line-width': 8, 'line-opacity': .9 } });
      map.addLayer({ id: 'drawing-line', type: 'line', source: 'drawing', layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': '#215c45', 'line-width': 4,
          'line-opacity': ['case', ['boolean', ['get', 'stale'], false], .55, 1],
          'line-dasharray': ['case', ['boolean', ['get', 'stale'], false], ['literal', [2, 1]],
            ['boolean', ['get', 'routed'], false], ['literal', [1, 0]], ['literal', [1, 2]]] } });
      map.addLayer({ id: 'drawing-arrows', type: 'symbol', source: 'drawing', filter: ['boolean', ['get', 'routed'], false], layout: arrowLayout });
      map.addSource('routes', { type: 'geojson', data: empty });
      map.addLayer({ id: 'route-halo', type: 'line', source: 'routes', layout: { 'line-cap': 'round', 'line-join': 'round',
        'line-sort-key': ['case', ['boolean', ['get', 'active'], false], 1, 0] },
        paint: { 'line-color': '#ffffff',
          'line-width': ['case', ['boolean', ['get', 'active'], false], 9, 7],
          'line-opacity': ['case', ['boolean', ['get', 'active'], false], .95, .7] } });
      map.addLayer({ id: 'route-lines', type: 'line', source: 'routes', layout: { 'line-cap': 'round', 'line-join': 'round',
        'line-sort-key': ['case', ['boolean', ['get', 'active'], false], 1, 0] },
        paint: { 'line-color': ['get', 'color'], 'line-width': ['case', ['boolean', ['get', 'active'], false], 6, 4.5],
          'line-opacity': ['case', ['boolean', ['get', 'active'], false], 1, .6],
          'line-dasharray': ['case', ['boolean', ['get', 'diagnostic'], false], ['literal', [2, 2]], ['literal', [1, 0]]] } });
      // Only the route in focus shows its direction, so overlapping options stay readable.
      map.addLayer({ id: 'route-arrows', type: 'symbol', source: 'routes', filter: ['boolean', ['get', 'active'], false], layout: arrowLayout });
      for (const layer of map.getStyle().layers) {
        const themed = themeOverlay(layer, latest.current.colorScheme);
        if (themed !== layer && themed.type === 'line' && themed.paint?.['line-color'])
          map.setPaintProperty(layer.id, 'line-color', themed.paint['line-color']);
        else if (themed !== layer && themed.type === 'fill' && themed.paint?.['fill-color'])
          map.setPaintProperty(layer.id, 'fill-color', themed.paint['fill-color']);
      }
      map.on('click', event => {
        if (Date.now() < ignoreMapClickUntil.current || event.originalEvent.target instanceof Element &&
          event.originalEvent.target.closest('.map-marker, .map-via-marker, .map-ghost-marker')) return;
        map.getCanvas().focus({ preventScroll: true });
        if (latest.current.mode !== 'browse') {
          latest.current.onPick({ latitude: event.lngLat.lat, longitude: ((event.lngLat.lng + 540) % 360) - 180 });
          return;
        }
        latest.current.onSelectPoint(null);
        const routes = map.queryRenderedFeatures(event.point, { layers: ['route-lines'] });
        const id = routes[0]?.properties?.id as string | undefined;
        if (id) latest.current.onSelect(id);
      });
      setReady(n => n + 1);
    });
    const observer = new ResizeObserver(() => map.resize());
    observer.observe(container.current);
    return () => {
      observer.disconnect(); markers.current.forEach(marker => marker.remove()); markers.current.clear();
      draggingMarker.current = null; ignoreMapClickUntil.current = 0; suppressFitAfterDrag.current = false;
      latest.current.onMarkerDragChange(false);
      map.remove(); mapRef.current = null;
    };
  }, [retry, configured]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready || appliedStyle.current === selectedStyle) return;
    try {
      // Swap the basemap while retaining live overlays and the current camera.
      map.setStyle(selectedStyle, { transformStyle: (previous, next) => preserveRouteOverlays(previous, next, props.colorScheme) });
      appliedStyle.current = selectedStyle;
    } catch { setError('map.styleFailed'); }
  }, [selectedStyle, props.colorScheme, ready]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready) return;
    const source = map.getSource('routes') as GeoJSONSource;
    source.setData({ type: 'FeatureCollection', features: props.candidates.map((candidate, i) => ({
      type: 'Feature', geometry: candidate.geometry,
      properties: { id: candidate.id, color: ROUTE_COLORS[i % ROUTE_COLORS.length],
        active: candidate.id === (props.highlightedId ?? props.selectedId), diagnostic: candidate.status === 'diagnostic' },
    })) });
  }, [props.candidates, props.highlightedId, props.selectedId, ready]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready) return;
    const visiblePreview = props.preview ?? props.previousPreview;
    const coordinates = visiblePreview?.geometry.coordinates ?? (props.start
      ? [[props.start.longitude, props.start.latitude], ...props.drawnPoints.map(p => [p.longitude, p.latitude])] : []);
    (map.getSource('drawing') as GeoJSONSource).setData(coordinates.length > 1 && props.candidates.length === 0 ? {
      type: 'FeatureCollection', features: [{ type: 'Feature', properties: { routed: !!visiblePreview, stale: !props.preview && !!props.previousPreview },
        geometry: { type: 'LineString', coordinates } }],
    } : empty);
    if (props.mode === 'draw') map.doubleClickZoom.disable(); else map.doubleClickZoom.enable();
    map.getCanvas().style.cursor = props.mode === 'draw' ? 'crosshair' : '';
  }, [props.preview, props.previousPreview, props.drawnPoints, props.start, props.candidates, props.mode, ready]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready) return;
    (map.getSource('radius') as GeoJSONSource).setData(props.start && props.radiusKm ? {
      type: 'FeatureCollection', features: [{ type: 'Feature', properties: {},
        geometry: { type: 'Polygon', coordinates: [radiusRing(props.start, props.radiusKm)] } }],
    } : empty);
  }, [props.start, props.radiusKm, ready]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready) return;
    const wanted = new Set<string>();
    function updateMarker(target: MapMarkerTarget, point: Location | null) {
      if (!point) return;
      const key = String(target); wanted.add(key);
      let marker = markers.current.get(key);
      if (!marker) {
        const element = document.createElement('div');
        element.className = typeof target === 'number' ? 'map-via-marker' : `map-marker ${target === 'end' ? 'end-marker' : ''}`;
        element.textContent = typeof target === 'number' ? String(target + 1) : target === 'start' ? 'A' : 'B';
        let touchPointer = false;
        element.addEventListener('pointerdown', event => { touchPointer = event.pointerType !== 'mouse'; });
        element.addEventListener('click', event => {
          event.stopPropagation(); map!.getCanvas().focus({ preventScroll: true });
          latest.current.onSelectPoint(typeof target === 'number' ? target : null);
        });
        const created = new maplibregl.Marker({ element, draggable: true }).setLngLat([point.longitude, point.latitude]).addTo(map!);
        created.on('dragstart', () => {
          map!.stop(); map!.getCanvas().focus({ preventScroll: true });
          draggingMarker.current = key; ignoreMapClickUntil.current = Infinity;
          suppressFitAfterDrag.current = true;
          if (touchPointer) liftAboveFinger(created);
          element.classList.toggle('is-lifted', touchPointer);
          element.classList.add('is-dragging'); latest.current.onMarkerDragChange(true); setDragging(true);
        });
        created.on('dragend', () => {
          draggingMarker.current = null; ignoreMapClickUntil.current = Date.now() + 150;
          element.classList.remove('is-dragging', 'is-lifted'); latest.current.onMarkerDragChange(false); setDragging(false);
          const position = created.getLngLat();
          latest.current.onMoveMarker(target, { latitude: position.lat, longitude: ((position.lng + 540) % 360) - 180 });
        });
        markers.current.set(key, created); marker = created;
      }
      // A preview response can arrive during a drag. Keep the marker instance and
      // its live position until the gesture ends instead of replacing it.
      if (draggingMarker.current !== key) marker.setLngLat([point.longitude, point.latitude]);
      const label = typeof target === 'number' ? t('map.drawnPoint', { n: target + 1 }) : t(target === 'start' ? 'start.label' : 'end.label');
      const element = marker.getElement();
      element.setAttribute('aria-label', label); element.title = t('map.dragToMove', { label });
      marker.getElement().classList.toggle('selected-point', typeof target === 'number' && target === props.selectedPoint);
    }
    updateMarker('start', props.start); updateMarker('end', props.end);
    props.drawnPoints.forEach((point, index) => updateMarker(index, props.preview?.snappedPoints?.[index + 1] ?? point));
    markers.current.forEach((marker, key) => {
      if (wanted.has(key)) return;
      marker.remove(); markers.current.delete(key);
      if (draggingMarker.current === key) {
        draggingMarker.current = null; ignoreMapClickUntil.current = Date.now() + 150;
        latest.current.onMarkerDragChange(false); setDragging(false);
      }
    });
  }, [props.start, props.end, props.drawnPoints, props.preview, props.selectedPoint, ready, t]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready || !props.editing || dragging || !props.start || !props.preview) return;
    const finish = props.end ?? props.start;
    const anchors = [props.start, ...props.drawnPoints.map((point, i) => props.preview?.snappedPoints?.[i + 1] ?? point), finish];
    ghosts.current = segmentMidpoints(anchors, props.preview.geometry.coordinates).map((point, index) => {
      const element = document.createElement('div');
      element.className = 'map-ghost-marker';
      element.setAttribute('aria-label', t('map.addBetween', { from: index === 0 ? t('map.fromStart') : t('map.pointN', { n: index }), to: index === anchors.length - 2 ? t('map.toFinish') : t('map.pointN', { n: index + 1 }) }));
      element.title = t('map.addTitle');
      const ghost = new maplibregl.Marker({ element, draggable: true }).setLngLat([point.longitude, point.latitude]).addTo(map);
      let touchPointer = false;
      element.addEventListener('pointerdown', event => { touchPointer = event.pointerType !== 'mouse'; });
      element.addEventListener('click', event => {
        event.stopPropagation();
        latest.current.onInsertPoint(index, point);
      });
      ghost.on('dragstart', () => {
        map.stop(); ignoreMapClickUntil.current = Infinity; suppressFitAfterDrag.current = true;
        if (touchPointer) liftAboveFinger(ghost);
        element.classList.add('is-dragging'); element.classList.toggle('is-lifted', touchPointer);
        latest.current.onMarkerDragChange(true);
      });
      ghost.on('dragend', () => {
        ignoreMapClickUntil.current = Date.now() + 150; latest.current.onMarkerDragChange(false);
        const position = ghost.getLngLat();
        latest.current.onInsertPoint(index, { latitude: position.lat, longitude: ((position.lng + 540) % 360) - 180 });
      });
      return ghost;
    });
    return () => { ghosts.current.forEach(marker => marker.remove()); ghosts.current = []; };
  }, [props.editing, props.start, props.end, props.drawnPoints, props.preview, dragging, ready, t]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready) return;
    const { top, right, bottom, left } = props.padding;
    map.setPadding({ top, right, bottom, left });
  }, [props.padding.top, props.padding.right, props.padding.bottom, props.padding.left, ready]);

  useEffect(() => {
    if (!mapRef.current || !ready) return;
    if (draggingMarker.current) return;
    if (suppressFitAfterDrag.current && !props.candidates.length) {
      if (props.preview || !props.drawnPoints.length) suppressFitAfterDrag.current = false;
      return;
    }
    suppressFitAfterDrag.current = false;
    if (props.mode === 'draw' && props.drawnPoints.length > 0) return;
    const routes = props.candidates.length ? props.candidates : props.preview ? [props.preview] : [];
    if (routes.length) {
      const bounds = new maplibregl.LngLatBounds();
      routes.forEach(route => route.geometry.coordinates.forEach(point => bounds.extend([point[0], point[1]])));
      mapRef.current.fitBounds(bounds, { padding: 48, maxZoom: 15, duration: 650 });
    } else if (props.start) mapRef.current.easeTo({ center: [props.start.longitude, props.start.latitude], zoom: 13, duration: 600 });
  }, [props.candidates, props.start, props.preview, props.mode, props.fitRequest, ready]);

  return <section className="map-panel" aria-label={t('map.label')}>
    <div ref={container} className="map-canvas" data-testid="route-map" />
    {props.mode !== 'browse' && <div className="map-instruction">{props.mode === 'draw'
      ? props.start ? t('map.tapToAdd') : t('map.tapToStart')
      : t(props.mode === 'start' ? 'map.tapToStart' : 'map.tapToFinish')}</div>}
    {(!configured || error) && <div className="map-message" role="alert">
      <strong>{t(error ? 'map.unavailable' : 'map.configure')}</strong>
      <p>{error ? t(error) : t('map.configureHint')}</p>
      {error && <button type="button" className="text-button" onClick={() => setRetry(n => n + 1)}>{t('map.retry')}</button>}
    </div>}
    {props.candidates.some(c => c.status === 'diagnostic') && <div className="map-diagnostic">{t('map.diagnostic')}</div>}
  </section>;
}
