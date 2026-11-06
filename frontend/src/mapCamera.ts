import { useEffect, useRef, type RefObject } from 'react';
import { LngLatBounds, type Map } from 'maplibre-gl';
import type { RouteCandidate } from './types';
import type { MapPadding } from './components/RouteMap';

// Shared by selected results and the editor's explicit “Fit all” action.
export function fitRoutes(map: Pick<Map, 'fitBounds'>, routes: RouteCandidate[], inset: MapPadding) {
  const bounds = new LngLatBounds();
  for (const route of routes) for (const [longitude, latitude] of route.geometry.coordinates) bounds.extend([longitude, latitude]);
  if (bounds.isEmpty()) return;
  // MapLibre's default adds fit padding to the map's existing overlay padding.
  // Use absolute padding so the sheet is counted once, not twice.
  map.fitBounds(bounds, { absolutePadding: true, padding: { top: inset.top + 48, right: inset.right + 48, bottom: inset.bottom + 48, left: inset.left + 48 }, maxZoom: 15, duration: 650 });
}

export function useSelectedRouteFit(mapRef: RefObject<Map | null>, ready: number, selected: RouteCandidate | null, padding: MapPadding) {
  const current = useRef({ selected, padding });
  current.current = { selected, padding };
  // Value identity, not array/object identity: panning, theme and unrelated renders must not reset the camera.
  const selectionKey = selected ? JSON.stringify([selected.id, selected.geometry.coordinates]) : null;
  useEffect(() => {
    if (!mapRef.current || !ready || !selectionKey) return;
    // Let the planner measure its results sheet before fitting the newly selected geometry.
    const frame = requestAnimationFrame(() => {
      const { selected, padding } = current.current;
      if (mapRef.current && selected) fitRoutes(mapRef.current, [selected], padding);
    });
    return () => cancelAnimationFrame(frame);
  }, [selectionKey, ready, mapRef]);
}
