import { ApiError, checked, connectUrl, disconnect, geocode, listConnections, sendRoute } from './api';

it('keeps ProblemDetails fields and Retry-After', async () => {
  const response = new Response(JSON.stringify({ detail: 'Try later', minimumDistanceKm: 6 }), { status: 503, headers: { 'Retry-After': '60' } });
  try { await checked(response); throw new Error('Expected rejection'); }
  catch (error) {
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problem.minimumDistanceKm).toBe(6);
    expect((error as ApiError).retryAfter).toBe('60');
  }
});

it('sends the language when geocoding', async () => {
  const fetchMock = vi.fn().mockResolvedValue(new Response('[]'));
  vi.stubGlobal('fetch', fetchMock);
  try {
    const signal = new AbortController().signal;
    await geocode('Utrecht', 'nl', signal);
    expect(fetchMock).toHaveBeenCalledWith('/api/geocode?q=Utrecht&lang=nl', { signal });
  } finally { vi.unstubAllGlobals(); }
});

it('builds connect URLs and calls the connection endpoints', async () => {
  expect(connectUrl('garmin', '/routes/s?route=r')).toBe('/api/connections/garmin/authorize?returnUrl=%2Froutes%2Fs%3Froute%3Dr');
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify([{ service: 'garmin', connected: true }]), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ externalId: '1', viewUrl: null }), { status: 200 }))
    .mockResolvedValueOnce(new Response(null, { status: 204 }));
  vi.stubGlobal('fetch', fetchMock);
  try {
    expect(await listConnections()).toEqual([{ service: 'garmin', connected: true }]);
    expect(await sendRoute('route-1', 'garmin', new AbortController().signal)).toEqual({ externalId: '1', viewUrl: null });
    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/routes/route-1/send/garmin', expect.objectContaining({ method: 'POST' }));
    await disconnect('garmin');
    expect(fetchMock).toHaveBeenNthCalledWith(3, '/api/connections/garmin', expect.objectContaining({ method: 'DELETE' }));
  } finally { vi.unstubAllGlobals(); }
});
