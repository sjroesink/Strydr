import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { I18nProvider } from './i18n';
import { ConnectionsPanel } from './components/ConnectionsPanel';
import { ExportMenu } from './components/ExportMenu';
import { LocationSearch } from './components/LocationSearch';

it('disables network-only location search, exports and Garmin authorization while offline', () => {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
  render(<I18nProvider>
    <LocationSearch label="Start location" value="Utrecht" place={null} placeholder="Search" onTextChange={vi.fn()} onPick={vi.fn()} />
    <ConnectionsPanel connections={[{ service: 'garmin', connected: false }]} returnUrl="/" onDisconnect={vi.fn()} />
    <ExportMenu busy={false} preparing={false} sending={false} services={[]} sent={{}} onExport={vi.fn()} onSend={vi.fn()} connectHref={() => '/api/authorize'} onConnect={vi.fn()} />
  </I18nProvider>);
  expect(screen.getByRole('combobox')).toBeDisabled();
  expect(screen.queryByRole('link', { name: /Garmin/ })).not.toBeInTheDocument();
  const button = screen.getByRole('button', { name: 'Export' });
  expect(button).toBeDisabled(); fireEvent.click(button);
  expect(screen.queryByRole('button', { name: /Download GPX/ })).not.toBeInTheDocument();
});
