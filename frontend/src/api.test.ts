import { ApiError, checked, geocode } from './api';

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
