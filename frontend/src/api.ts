import type { Language } from './i18n';
import type { ApiProblem, RouteCandidate, RouteEditSession, RoutePreviewRequest, RouteRequest, SavedRouteSearch, SentRoute, SentRouteRecord, ServiceConnectionStatus, ServiceKind, Suggestion } from './types';

export class ApiError extends Error {
  problem: ApiProblem;
  retryAfter: string | null;
  constructor(problem: ApiProblem, retryAfter: string | null = null) {
    super(problem.detail ?? problem.title ?? 'Unable to complete the request. Please try again.');
    this.problem = problem;
    this.retryAfter = retryAfter;
  }
}

function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  if (navigator.onLine === false) return Promise.reject(new ApiError({ code: 'offline' }));
  // Connection state and saved routes must not enter the browser HTTP cache either.
  return fetch(input, { ...init, cache: 'no-store' });
}

export async function checked(response: Response): Promise<Response> {
  if (response.ok) return response;
  let problem: ApiProblem;
  try { problem = await response.json() as ApiProblem; }
  catch { problem = { title: 'Service unavailable', detail: 'Could not reach Meander. Check that the API is running.', code: 'apiUnreachable' }; }
  throw new ApiError({ ...problem, status: response.status }, response.headers.get('Retry-After'));
}
export async function generate(request: RouteRequest, signal: AbortSignal): Promise<RouteCandidate[]> {
  const response = await checked(await apiFetch('/api/routes/generate', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal,
  }));
  return response.json() as Promise<RouteCandidate[]>;
}
export async function geocode(query: string, language: Language, signal: AbortSignal): Promise<Suggestion[]> {
  return (await checked(await apiFetch(`/api/geocode?q=${encodeURIComponent(query)}&lang=${language}`, { signal }))).json() as Promise<Suggestion[]>;
}
export async function loadSearch(id: string, signal: AbortSignal): Promise<SavedRouteSearch> {
  return (await checked(await apiFetch(`/api/searches/${encodeURIComponent(id)}`, { signal }))).json() as Promise<SavedRouteSearch>;
}
export async function previewRoute(request: RoutePreviewRequest, signal: AbortSignal): Promise<RouteCandidate> {
  return (await checked(await apiFetch('/api/routes/preview', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal,
  }))).json() as Promise<RouteCandidate>;
}
export async function editRoute(id: string, signal: AbortSignal): Promise<RouteEditSession> {
  return (await checked(await apiFetch(`/api/routes/${encodeURIComponent(id)}/edit`, { method: 'POST', signal }))).json() as Promise<RouteEditSession>;
}
export async function saveRoute(request: RouteRequest, signal: AbortSignal): Promise<RouteCandidate[]> {
  return (await checked(await apiFetch('/api/routes/save', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal,
  }))).json() as Promise<RouteCandidate[]>;
}
async function downloadRoute(candidate: RouteCandidate, format: 'gpx' | 'fit', signal: AbortSignal) {
  const response = await checked(await apiFetch(`/api/routes/${candidate.id}/${format}`, { signal }));
  const url = URL.createObjectURL(await response.blob());
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `meander-${candidate.distanceKm.toFixed(1)}km.${format}`;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export const downloadGpx = (candidate: RouteCandidate, signal: AbortSignal) => downloadRoute(candidate, 'gpx', signal);
export const downloadFit = (candidate: RouteCandidate, signal: AbortSignal) => downloadRoute(candidate, 'fit', signal);
export async function listConnections(signal?: AbortSignal): Promise<ServiceConnectionStatus[]> {
  return (await checked(await apiFetch('/api/connections', { signal }))).json() as Promise<ServiceConnectionStatus[]>;
}
export const connectUrl = (service: ServiceKind, returnUrl: string) =>
  `/api/connections/${service}/authorize?returnUrl=${encodeURIComponent(returnUrl)}`;
export async function disconnect(service: ServiceKind) {
  await checked(await apiFetch(`/api/connections/${service}`, { method: 'DELETE' }));
}
export async function listSent(signal?: AbortSignal): Promise<SentRouteRecord[]> {
  return (await checked(await apiFetch('/api/routes/sent', { signal }))).json() as Promise<SentRouteRecord[]>;
}
export async function sendRoute(id: string, service: ServiceKind, signal: AbortSignal): Promise<SentRoute> {
  return (await checked(await apiFetch(`/api/routes/${encodeURIComponent(id)}/send/${service}`, { method: 'POST', signal }))).json() as Promise<SentRoute>;
}
