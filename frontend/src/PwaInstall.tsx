import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useI18n } from './i18n';

type InstallPrompt = Event & { prompt(): Promise<void>; userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }> };
const standalone = () => window.matchMedia?.('(display-mode: standalone)').matches
  || (navigator as Navigator & { standalone?: boolean }).standalone === true;
const ios = () => /iPad|iPhone|iPod/.test(navigator.userAgent) || navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;

const InstallContext = createContext<ReactNode>(null);

// Capture browser events even while the menu is closed or the route editor is open.
export function PwaInstallProvider({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  const [installed, setInstalled] = useState(standalone);
  const [install, setInstall] = useState<InstallPrompt | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const offer = (event: Event) => { event.preventDefault(); setInstall(event as InstallPrompt); };
    const done = () => { setInstalled(true); setInstall(null); };
    window.addEventListener('beforeinstallprompt', offer); window.addEventListener('appinstalled', done);
    return () => { window.removeEventListener('beforeinstallprompt', offer); window.removeEventListener('appinstalled', done); };
  }, []);
  async function installApp() {
    if (!install) return;
    setFailed(false);
    try { await install.prompt(); const choice = await install.userChoice; if (choice.outcome === 'accepted') setInstalled(true); }
    catch { setFailed(true); }
    finally { setInstall(null); }
  }
  const controls = <>
    {!installed && install && <button type="button" className="chip-button settings-install" onClick={() => void installApp()}>{t('pwa.install')}</button>}
    {!installed && ios() && !install && <details className="pwa-ios"><summary>{t('pwa.install')}</summary><p>{t('pwa.ios')}</p></details>}
    {failed && <p role="status">{t('pwa.installFailed')}</p>}
  </>;
  return <InstallContext value={controls}>{children}</InstallContext>;
}

export function PwaInstall() { return useContext(InstallContext); }
