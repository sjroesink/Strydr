import { ApiError, checked } from './api';

it('keeps ProblemDetails fields and Retry-After', async () => {
  const response = new Response(JSON.stringify({ detail: 'Try later', minimumDistanceKm: 6 }), { status: 503, headers: { 'Retry-After': '60' } });
  try { await checked(response); throw new Error('Expected rejection'); }
  catch (error) {
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problem.minimumDistanceKm).toBe(6);
    expect((error as ApiError).retryAfter).toBe('60');
  }
});
