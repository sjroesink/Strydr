import { useCallback, useEffect, useState } from 'react';
import { disconnect as disconnectService, listConnections } from './api';
import type { ServiceConnectionStatus, ServiceKind } from './types';

export const SERVICE_NAMES: Record<ServiceKind, string> = { garmin: 'Garmin' };

export function useConnections() {
  const [connections, setConnections] = useState<ServiceConnectionStatus[]>([]);
  useEffect(() => {
    const controller = new AbortController();
    // Integrations are optional: an unreachable list just hides them.
    listConnections(controller.signal).then(list => setConnections(list ?? [])).catch(() => { if (!controller.signal.aborted) setConnections([]); });
    return () => controller.abort();
  }, []);
  const setConnected = useCallback((service: ServiceKind, connected: boolean) =>
    setConnections(list => list.map(item => item.service === service ? { ...item, connected } : item)), []);
  const disconnect = useCallback(async (service: ServiceKind) => {
    await disconnectService(service);
    setConnected(service, false);
  }, [setConnected]);
  return { connections, setConnected, disconnect };
}
