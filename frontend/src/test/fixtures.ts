import type { RouteCandidate } from '../types';

const point = { latitude: 52.09, longitude: 5.12 };
export function candidate(index = 1): RouteCandidate {
  return {
    id: `route-${index}`, expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
    geometry: { type: 'LineString', coordinates: [[5.12, 52.09], [5.13, 52.1], [5.12, 52.09]] },
    distanceKm: 5 + index / 100, elevationGainM: 20, maxDistanceFromStartKm: 1.5,
    score: .01, status: 'qualified', start: { requested: point, snapped: point, snapDistanceM: 0 },
    end: { requested: point, snapped: point, snapDistanceM: 0 }, warnings: [],
  };
}
