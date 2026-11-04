import { expect, test } from 'vitest';
import { readConnectReturn } from './connectReturn';

test('reads the service a visitor returns from and whether they asked to send', () => {
  expect(readConnectReturn('?route=r&connected=garmin')).toEqual({ service: 'garmin', failed: false, send: false });
  expect(readConnectReturn('?route=r&send=garmin&connected=garmin')).toEqual({ service: 'garmin', failed: false, send: true });
  expect(readConnectReturn('?route=r&send=strava&connected=garmin')).toEqual({ service: 'garmin', failed: false, send: false });
  expect(readConnectReturn('?connectError=garmin')).toEqual({ service: 'garmin', failed: true, send: false });
  expect(readConnectReturn('?connected=strava')).toBeNull();
  expect(readConnectReturn('?route=r')).toBeNull();
});
