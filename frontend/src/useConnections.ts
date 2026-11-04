import { useCallback, useEffect, useState } from 'react';
import { disconnect as disconnectService, listConnections, listSent } from './api';
import type { SentViews } from './components/ExportMenu';
import type { ServiceConnectionStatus, ServiceKind } from './types';

export const SERVICE_NAMES: Record<ServiceKind, string> = { garmin: 'Garmin' };
// Where a sent route can be viewed.
export const SERVICE_APPS: Record<ServiceKind, string> = { garmin: 'Garmin Connect' };

export function useConnections() {
  const [connections, setConnections] = useState<ServiceConnectionStatus[]>([]);
  // Per route id: where each service shows it. The server remembers this per browser session.
  const [sent, setSent] = useState<Record<string, SentViews>>({});
  useEffect(() => {
    const controller = new AbortController();
    // Integrations are optional: an unreachable list just hides them.
    listConnections(controller.signal).then(list => setConnections(list ?? [])).catch(() => { if (!controller.signal.aborted) setConnections([]); });
    listSent(controller.signal).then(list => setSent(all => {
      const merged = { ...all };
      for (const { routeId, service, viewUrl } of list ?? []) merged[routeId] = { ...merged[routeId], [service]: viewUrl };
      return merged;
    })).catch(() => {});
    return () => controller.abort();
  }, []);
  const setConnected = useCallback((service: ServiceKind, connected: boolean) =>
    setConnections(list => list.map(item => item.service === service ? { ...item, connected } : item)), []);
  const markSent = useCallback((routeId: string, service: ServiceKind, viewUrl: string | null) =>
    setSent(all => ({ ...all, [routeId]: { ...all[routeId], [service]: viewUrl } })), []);
  const disconnect = useCallback(async (service: ServiceKind) => {
    await disconnectService(service);
    setConnected(service, false);
    // Disconnecting also makes the server forget what was sent to this service.
    setSent(all => Object.fromEntries(Object.entries(all).map(([id, views]) => {
      const { [service]: _, ...rest } = views;
      return [id, rest];
    })));
  }, [setConnected]);
  return { connections, setConnected, disconnect, sent, markSent };
}
