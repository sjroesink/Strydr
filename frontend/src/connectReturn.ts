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
