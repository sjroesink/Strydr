import { useSyncExternalStore } from 'react';

function subscribeOnline(callback: () => void) {
  window.addEventListener('online', callback); window.addEventListener('offline', callback);
  return () => { window.removeEventListener('online', callback); window.removeEventListener('offline', callback); };
}
export function useOnline() {
  return useSyncExternalStore(subscribeOnline, () => navigator.onLine !== false);
}

export async function registerShell(enabled = import.meta.env.PROD): Promise<ServiceWorkerRegistration | null> {
  if (!enabled || !('serviceWorker' in navigator)) return null;
  try { return await navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' }); }
  catch { return null; } // The online planner remains usable when installation/storage is unavailable.
}

// Only the tab whose user explicitly accepted the warning reloads. Other open planners stay intact.
export function activateUpdate(worker: ServiceWorker, reload = () => window.location.reload()) {
  navigator.serviceWorker.addEventListener('controllerchange', reload, { once: true });
  worker.postMessage({ type: 'ACTIVATE_UPDATE' });
}
