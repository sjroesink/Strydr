import { insertionIndex, segmentMidpoints } from './routeEditing';

it('inserts before, between and after existing route points', () => {
  const start = { latitude: 0, longitude: 0 }, end = { latitude: 0, longitude: 3 };
  const points = [{ latitude: 0, longitude: 1 }, { latitude: 0, longitude: 2 }];
  expect(insertionIndex({ latitude: .01, longitude: .4 }, start, points, end)).toBe(0);
  expect(insertionIndex({ latitude: .01, longitude: 1.4 }, start, points, end)).toBe(1);
  expect(insertionIndex({ latitude: .01, longitude: 2.4 }, start, points, end)).toBe(2);
});

it('follows winding geometry instead of a chord through the middle of the route', () => {
  const start = { latitude: 0, longitude: 0 }, end = { latitude: 0, longitude: 0 };
  const points = [{ latitude: 0, longitude: 2 }, { latitude: 2, longitude: 2 }];
  const geometry = [[0, 0], [0, 1], [1, 1], [2, 1], [2, 0], [3, 0], [3, 2], [2, 2], [0, 2], [0, 0]];
  expect(insertionIndex({ latitude: 1, longitude: .8 }, start, points, end, geometry)).toBe(0);
  expect(insertionIndex({ latitude: 1, longitude: 3 }, start, points, end, geometry)).toBe(1);
  expect(insertionIndex({ latitude: 2, longitude: .5 }, start, points, end, geometry)).toBe(2);
});

it('places a midpoint handle halfway along each routed segment', () => {
  const anchors = [{ latitude: 0, longitude: 0 }, { latitude: 0, longitude: 2 }, { latitude: 0, longitude: 0 }];
  // Out along the equator, then back via a detour north.
  const geometry = [[0, 0], [1, 0], [2, 0], [2, 1], [0, 1], [0, 0]];
  const [first, second] = segmentMidpoints(anchors, geometry);
  expect(first.longitude).toBeCloseTo(1); expect(first.latitude).toBeCloseTo(0);
  expect(second.longitude).toBeCloseTo(1); expect(second.latitude).toBeCloseTo(1);
  expect(segmentMidpoints([anchors[0]])).toEqual([]);
});
