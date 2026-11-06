import { expect, it, vi } from 'vitest';
import { geocode, listConnections, loadSearch, sendRoute, disconnect } from './api';
it('rejects offline network actions with a translatable error without dispatching API or Garmin requests', async () => {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
  const fetch = vi.fn().mockResolvedValue(new Response('[]')); vi.stubGlobal('fetch', fetch);
  const signal = new AbortController().signal;
  for (const action of [() => geocode('Utrecht', 'nl', signal), () => loadSearch('private', signal), () => listConnections(), () => sendRoute('route', 'garmin', signal), () => disconnect('garmin')]) {
    await expect(action()).rejects.toMatchObject({ problem: { code: 'offline' } });
  }
  expect(fetch).not.toHaveBeenCalled();
});
