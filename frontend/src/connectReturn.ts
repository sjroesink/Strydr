import type { ServiceKind } from './types';

const SERVICES: readonly ServiceKind[] = ['garmin'];
export type ConnectReturn = { service: ServiceKind; failed: boolean; send: boolean };

// The API returns from a service's consent screen with ?connected= or ?connectError=; send=<service> means the visitor connected in order to send a route.
export function readConnectReturn(search: string): ConnectReturn | null {
  const params = new URLSearchParams(search);
  const failedService = params.get('connectError');
  const service = (failedService ?? params.get('connected')) as ServiceKind | null;
  return service && SERVICES.includes(service) ? { service, failed: failedService !== null, send: params.get('send') === service } : null;
}

const INTENT_KEY = 'meander.pendingSend';
const INTENT_MAX_AGE_MS = 15 * 60 * 1000;

// Remembered when the visitor clicks "Send to ..." before connecting, so a return URL alone can never trigger a send.
export function rememberSendIntent(service: ServiceKind, routeId: string) {
  try { window.sessionStorage.setItem(INTENT_KEY, JSON.stringify({ service, routeId, at: Date.now() })); } catch { /* without it nothing is sent after connecting */ }
}

// Returns the remembered route for this service when it is recent, and always forgets the intent.
export function takeSendIntent(service: ServiceKind): string | null {
  try {
    const raw = window.sessionStorage.getItem(INTENT_KEY);
    window.sessionStorage.removeItem(INTENT_KEY);
    const intent = raw ? JSON.parse(raw) as { service?: unknown; routeId?: unknown; at?: unknown } : null;
    return intent && intent.service === service && typeof intent.routeId === 'string' && typeof intent.at === 'number'
      && Date.now() - intent.at >= 0 && Date.now() - intent.at < INTENT_MAX_AGE_MS ? intent.routeId : null;
  } catch { return null; }
}
