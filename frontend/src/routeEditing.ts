import type { Location } from './types';

// Follow the actual routed geometry when finding the insertion position, rather
// than using straight marker-to-marker chords across a winding path.
export function insertionIndex(location: Location, start: Location, points: Location[], end: Location,
  coordinates?: number[][]): number {
  const anchors = [start, ...points, end];
  const geometry = coordinates?.length ? coordinates.map(p => ({ longitude: p[0], latitude: p[1] })) : anchors;
  const cos = Math.cos(location.latitude * Math.PI / 180);
  const xy = (point: Location) => [((point.longitude - location.longitude + 540) % 360 - 180) * cos, point.latitude - location.latitude];
  const distance = (a: Location, b: Location) => {
    const [x, y] = xy(a); const [u, v] = xy(b); return (x - u) ** 2 + (y - v) ** 2;
  };
  const indices = [0];
  for (const anchor of points) {
    let best = Math.min(indices[indices.length - 1] + 1, geometry.length - 1);
    for (let i = best + 1; i < geometry.length - 1; i++)
      if (distance(geometry[i], anchor) < distance(geometry[best], anchor)) best = i;
    indices.push(best);
  }
  let edge = 0, nearest = Infinity;
  for (let i = 0; i < geometry.length - 1; i++) {
    const [x, y] = xy(geometry[i]), [u, v] = xy(geometry[i + 1]);
    const dx = u - x, dy = v - y;
    const t = Math.max(0, Math.min(1, -(x * dx + y * dy) / (dx * dx + dy * dy || 1)));
    const squared = (x + t * dx) ** 2 + (y + t * dy) ** 2;
    if (squared < nearest) { nearest = squared; edge = i; }
  }
  return Math.max(0, indices.filter(i => i <= edge).length - 1);
}

// Halfway points along the routed line between consecutive anchors. Each one is
// a handle for inserting a new control point into that segment.
export function segmentMidpoints(anchors: Location[], coordinates?: number[][]): Location[] {
  if (anchors.length < 2) return [];
  const geometry = coordinates?.length ? coordinates.map(p => ({ longitude: p[0], latitude: p[1] })) : anchors;
  const cos = Math.cos(anchors[0].latitude * Math.PI / 180);
  const distance = (a: Location, b: Location) => Math.hypot((a.longitude - b.longitude) * cos, a.latitude - b.latitude);
  const indices = [0];
  for (const anchor of anchors.slice(1, -1)) {
    let best = Math.min(indices[indices.length - 1] + 1, geometry.length - 1);
    for (let i = best + 1; i < geometry.length - 1; i++)
      if (distance(geometry[i], anchor) < distance(geometry[best], anchor)) best = i;
    indices.push(best);
  }
  indices.push(geometry.length - 1);
  return indices.slice(0, -1).map((from, segment) => {
    const to = indices[segment + 1];
    if (to <= from) {
      const a = anchors[segment], b = anchors[segment + 1];
      return { latitude: (a.latitude + b.latitude) / 2, longitude: (a.longitude + b.longitude) / 2 };
    }
    let total = 0;
    for (let i = from; i < to; i++) total += distance(geometry[i], geometry[i + 1]);
    let remaining = total / 2;
    for (let i = from; i < to; i++) {
      const length = distance(geometry[i], geometry[i + 1]);
      if (remaining <= length || i === to - 1) {
        const t = length ? Math.min(1, remaining / length) : 0;
        return { latitude: geometry[i].latitude + (geometry[i + 1].latitude - geometry[i].latitude) * t,
          longitude: geometry[i].longitude + (geometry[i + 1].longitude - geometry[i].longitude) * t };
      }
      remaining -= length;
    }
    return geometry[from];
  });
}
