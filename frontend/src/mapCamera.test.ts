import { act, renderHook } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import type { Map } from 'maplibre-gl';
import { candidate } from './test/fixtures';
import { fitRoutes, useSelectedRouteFit } from './mapCamera';
const padding = { top: 64, right: 0, bottom: 360, left: 0 };
it('fits all selected geometry coordinates with overlay clearance and the editor zoom cap', () => {
  const fitBounds = vi.fn();
  fitRoutes({ fitBounds } as unknown as Map, [{ ...candidate(), geometry: { type: 'LineString', coordinates: [[5, 52], [6, 51], [4, 53], [5, 52]] } }], padding);
  const [bounds, options] = fitBounds.mock.calls[0];
  expect(bounds.toArray()).toEqual([[4, 51], [6, 53]]);
  expect(options).toMatchObject({ absolutePadding: true, maxZoom: 15, duration: 650, padding: { top: 112, right: 48, bottom: 408, left: 48 } });
});
it('fits initial/restored selection and route changes only after map readiness, not unrelated renders or padding/theme updates', () => {
  const callbacks: FrameRequestCallback[] = [];
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callbacks.push(callback); return callbacks.length; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
  const fitBounds = vi.fn(); const mapRef = { current: { fitBounds } as unknown as Map };
  const route = candidate();
  const { rerender } = renderHook(({ ready, selected, inset }) => useSelectedRouteFit(mapRef, ready, selected, inset),
    { initialProps: { ready: 0, selected: route, inset: padding } });
  expect(callbacks).toHaveLength(0);
  rerender({ ready: 1, selected: route, inset: padding });
  act(() => callbacks.pop()!(0)); expect(fitBounds).toHaveBeenCalledTimes(1);
  rerender({ ready: 1, selected: { ...route }, inset: { ...padding, bottom: 400 } });
  expect(callbacks).toHaveLength(0);
  rerender({ ready: 1, selected: { ...route, id: 'route-2' }, inset: padding });
  act(() => callbacks.pop()!(0)); expect(fitBounds).toHaveBeenCalledTimes(2);
  rerender({ ready: 1, selected: { ...route, id: 'route-3' }, inset: padding });
  act(() => callbacks.pop()!(0)); expect(fitBounds).toHaveBeenCalledTimes(3);
  rerender({ ready: 1, selected: { ...route, id: 'route-3', geometry: { type: 'LineString', coordinates: [[3, 50], [4, 51]] } }, inset: padding });
  act(() => callbacks.pop()!(0)); expect(fitBounds).toHaveBeenCalledTimes(4);
});
