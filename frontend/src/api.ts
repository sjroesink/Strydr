import type { Language } from './i18n';
import type { ApiProblem, RouteCandidate, RouteEditSession, RoutePreviewRequest, RouteRequest, SavedRouteSearch, Suggestion } from './types';

export class ApiError extends Error {
  problem: ApiProblem;
  retryAfter: string | null;
  constructor(problem: ApiProblem, retryAfter: string | null = null) {
    super(problem.detail ?? problem.title ?? 'Unable to complete the request. Please try again.');
    this.problem = problem;
    this.retryAfter = retryAfter;
  }
}

export async function checked(response: Response): Promise<Response> {
  if (response.ok) return response;
  let problem: ApiProblem;
  try { problem = await response.json() as ApiProblem; }
  catch { problem = { title: 'Service unavailable', detail: 'Could not reach Meander. Check that the API is running.' }; }
  throw new ApiError({ ...problem, status: response.status }, response.headers.get('Retry-After'));
}
export async function generate(request: RouteRequest, signal: AbortSignal): Promise<RouteCandidate[]> {
  const response = await checked(await fetch('/api/routes/generate', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal,
  }));
  return response.json() as Promise<RouteCandidate[]>;
}
export async function geocode(query: string, language: Language, signal: AbortSignal): Promise<Suggestion[]> {
  return (await checked(await fetch(`/api/geocode?q=${encodeURIComponent(query)}&lang=${language}`, { signal }))).json() as Promise<Suggestion[]>;
}
export async function loadSearch(id: string, signal: AbortSignal): Promise<SavedRouteSearch> {
  return (await checked(await fetch(`/api/searches/${encodeURIComponent(id)}`, { signal }))).json() as Promise<SavedRouteSearch>;
}
export async function previewRoute(request: RoutePreviewRequest, signal: AbortSignal): Promise<RouteCandidate> {
  return (await checked(await fetch('/api/routes/preview', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal,
  }))).json() as Promise<RouteCandidate>;
}
export async function editRoute(id: string, signal: AbortSignal): Promise<RouteEditSession> {
  return (await checked(await fetch(`/api/routes/${encodeURIComponent(id)}/edit`, { method: 'POST', signal }))).json() as Promise<RouteEditSession>;
}
export async function saveRoute(request: RouteRequest, signal: AbortSignal): Promise<RouteCandidate[]> {
  return (await checked(await fetch('/api/routes/save', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal,
  }))).json() as Promise<RouteCandidate[]>;
}
export async function downloadGpx(candidate: RouteCandidate, signal: AbortSignal) {
  const response = await checked(await fetch(`/api/routes/${candidate.id}/gpx`, { signal }));
  const url = URL.createObjectURL(await response.blob());
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `meander-${candidate.distanceKm.toFixed(1)}km.gpx`;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
