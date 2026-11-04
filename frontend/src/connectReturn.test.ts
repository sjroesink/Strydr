import { expect, test } from 'vitest';
import { readConnectReturn } from './connectReturn';

test('reads the service a visitor returns from', () => {
  expect(readConnectReturn('?route=r&connected=garmin')).toEqual({ service: 'garmin', failed: false });
  expect(readConnectReturn('?connectError=garmin')).toEqual({ service: 'garmin', failed: true });
  expect(readConnectReturn('?connected=strava')).toBeNull();
  expect(readConnectReturn('?route=r')).toBeNull();
});
