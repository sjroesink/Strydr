import { useEffect, useId, useRef, useState } from 'react';
import { useI18n } from '../i18n';
import type { ServiceConnectionStatus, ServiceKind } from '../types';
import { SERVICE_APPS, SERVICE_NAMES } from '../useConnections';
import { Icon } from './Icon';

export type ExportFormat = 'gpx' | 'fit';
// A sent route remembers where the service shows it; null when the service gave no link.
export type SentViews = Partial<Record<ServiceKind, string | null>>;

// One disclosure for every way a route leaves Meander: files and connected services.
export function ExportMenu({ busy, preparing, sending, services, sent, onExport, onSend, connectHref, onConnect }: {
  busy: boolean; preparing: boolean; sending: boolean;
  services: ServiceConnectionStatus[]; sent: SentViews;
  onExport(format: ExportFormat): void; onSend(service: ServiceKind): void;
  connectHref(service: ServiceKind): string; onConnect(service: ServiceKind): void;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLUListElement>(null);
  const panelId = useId();

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      const target = event.target as Node;
      if (!trigger.current?.contains(target) && !panel.current?.contains(target)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      setOpen(false); trigger.current?.focus();
    };
    document.addEventListener('pointerdown', outside);
    document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };
  }, [open]);

  const choose = (action: () => void) => () => { setOpen(false); action(); };
  const label = preparing ? t('route.preparing') : sending ? t('route.sending') : t('route.export');

  return <>
    <button ref={trigger} type="button" className="route-action route-action-download" disabled={busy}
      aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(value => !value)}>
      <Icon name="download" />{label}<Icon name="chevron" className="export-chevron" />
    </button>
    {open && <ul ref={panel} id={panelId} className="export-menu">
      <li><button type="button" onClick={choose(() => onExport('gpx'))}>
        <Icon name="download" /><span>{t('route.download')}<small>{t('route.downloadHint')}</small></span></button></li>
      <li><button type="button" onClick={choose(() => onExport('fit'))}>
        <Icon name="download" /><span>{t('route.downloadFit')}<small>{t('route.downloadFitHint')}</small></span></button></li>
      {services.map(({ service, connected }) => {
        const view = sent[service];
        if (view) return <li key={service}><a href={view} target="_blank" rel="noopener noreferrer" onClick={() => setOpen(false)}>
          <Icon name="external" /><span>{t('route.viewIn', { app: SERVICE_APPS[service] })}</span></a></li>;
        const name = t('route.sendTo', { service: SERVICE_NAMES[service] });
        return <li key={service}>{connected
          ? <button type="button" onClick={choose(() => onSend(service))}><Icon name="send" /><span>{name}</span></button>
          : <a href={connectHref(service)} onClick={() => onConnect(service)}>
            <Icon name="send" /><span>{name}<small>{t('route.connectFirst')}</small></span></a>}</li>;
      })}
    </ul>}
  </>;
}

// Shown on the card after a send, so the result stays visible next to the route it belongs to.
export function SentNotes({ sent }: { sent: SentViews }) {
  const { t } = useI18n();
  const entries = Object.entries(sent) as [ServiceKind, string | null][];
  if (entries.length === 0) return null;
  return <>{entries.map(([service, view]) => <p key={service} className="route-sent" role="status">
    <Icon name="check" /><span>{t('route.sent', { service: SERVICE_NAMES[service] })}</span>
    {view && <a href={view} target="_blank" rel="noopener noreferrer">{t('route.viewIn', { app: SERVICE_APPS[service] })}<Icon name="external" /></a>}
  </p>)}</>;
}
