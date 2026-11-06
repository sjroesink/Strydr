import './pwa.css';
import { useEffect, useState } from 'react';
import { useI18n } from './i18n';
import { activateUpdate, registerShell, useOnline } from './pwa';

export function PwaStatus() {
  const { t } = useI18n();
  const online = useOnline();
  const [waiting, setWaiting] = useState<ServiceWorker | null>(null);
  useEffect(() => {
    let disposed = false;
    let registration: ServiceWorkerRegistration | null = null;
    let installing: ServiceWorker | null = null;
    const check = () => {
      if (disposed) return;
      // The first installation also briefly passes through waiting. It isn't an update.
      setWaiting(navigator.serviceWorker.controller && registration?.waiting
        && registration.waiting !== navigator.serviceWorker.controller ? registration.waiting : null);
    };
    const stateChanged = () => { if (installing?.state === 'installed') check(); };
    const updateFound = () => {
      installing?.removeEventListener('statechange', stateChanged);
      installing = registration?.installing ?? null;
      installing?.addEventListener('statechange', stateChanged);
    };
    const refresh = () => { if (document.visibilityState === 'visible' && navigator.onLine) void registration?.update().catch(() => {}); };
    void registerShell().then(result => {
      if (disposed || !result) return;
      registration = result; check(); updateFound();
      registration.addEventListener('updatefound', updateFound);
      navigator.serviceWorker.addEventListener('controllerchange', check);
      document.addEventListener('visibilitychange', refresh);
    });
    return () => {
      disposed = true; registration?.removeEventListener('updatefound', updateFound);
      if ('serviceWorker' in navigator) navigator.serviceWorker.removeEventListener('controllerchange', check);
      installing?.removeEventListener('statechange', stateChanged); document.removeEventListener('visibilitychange', refresh);
    };
  }, []);
  return <aside className="pwa-status" aria-label={t('pwa.title')}>
    {!online && <p className="pwa-notice" role="status">{t('pwa.offline')}</p>}
    {waiting && <div className="pwa-notice"><p>{t('pwa.updateReady')}</p>
      <button type="button" className="chip-button" disabled={!online} onClick={() => {
        if (window.confirm(t('pwa.updateConfirm'))) activateUpdate(waiting);
      }}>{t('pwa.update')}</button></div>}
  </aside>;
}
