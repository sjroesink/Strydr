import { connectUrl } from '../api';
import { useI18n } from '../i18n';
import type { ServiceConnectionStatus, ServiceKind } from '../types';
import { SERVICE_NAMES } from '../useConnections';
import { Icon } from './Icon';

export function ConnectionsPanel({ connections, returnUrl, onDisconnect }: {
  connections: ServiceConnectionStatus[]; returnUrl: string; onDisconnect(service: ServiceKind): void;
}) {
  const { t } = useI18n();
  if (connections.length === 0) return null;
  return <section className="connections" aria-label={t('connections.title')}>
    {connections.map(({ service, connected }) => <p key={service} className="export-note">
      <Icon name="send" />
      {connected ? <>
        <span>{t('connections.connected', { service: SERVICE_NAMES[service] })}</span>
        <button type="button" className="text-link" onClick={() => onDisconnect(service)}>{t('connections.disconnect')}</button>
      </> : <a href={connectUrl(service, returnUrl)}>{t('connections.connect', { service: SERVICE_NAMES[service] })}</a>}
    </p>)}
  </section>;
}
