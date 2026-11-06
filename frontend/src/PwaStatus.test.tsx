import { act, fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { I18nProvider } from './i18n';
import { PwaStatus } from './PwaStatus';
import { PwaInstall, PwaInstallProvider } from './PwaInstall';
import { registerShell, activateUpdate } from './pwa';

it('shows an honest offline notice in both languages and removes it on reconnect', () => {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
  localStorage.setItem('meander.language', 'nl');
  const { unmount } = render(<I18nProvider><PwaStatus /></I18nProvider>);
  expect(screen.getByRole('status')).toHaveTextContent('Je bent offline');
  expect(screen.getByRole('status')).toHaveTextContent('kaarten');
  unmount(); localStorage.setItem('meander.language', 'en');
  render(<I18nProvider><PwaStatus /></I18nProvider>);
  expect(screen.getByRole('status')).toHaveTextContent('You are offline');
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true);
  fireEvent(window, new Event('online'));
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
});

it('only offers native installation after a supported event and hides it once installed', async () => {
  render(<I18nProvider><PwaInstallProvider><PwaInstall /></PwaInstallProvider></I18nProvider>);
  expect(screen.queryByRole('button', { name: 'Install Meander' })).not.toBeInTheDocument();
  const prompt = vi.fn().mockResolvedValue(undefined);
  const event = Object.assign(new Event('beforeinstallprompt', { cancelable: true }), { prompt, userChoice: Promise.resolve({ outcome: 'accepted' }) });
  fireEvent(window, event);
  expect(event.defaultPrevented).toBe(true);
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Install Meander' })); });
  expect(prompt).toHaveBeenCalledOnce();
  fireEvent(window, new Event('appinstalled'));
  expect(screen.queryByRole('button', { name: 'Install Meander' })).not.toBeInTheDocument();
});

it('retains a deferred browser install offer while the options UI is unmounted for route editing', async () => {
  const { rerender } = render(<I18nProvider><PwaInstallProvider><span>Editor</span></PwaInstallProvider></I18nProvider>);
  const prompt = vi.fn().mockResolvedValue(undefined);
  fireEvent(window, Object.assign(new Event('beforeinstallprompt', { cancelable: true }), {
    prompt, userChoice: Promise.resolve({ outcome: 'dismissed' }),
  }));
  rerender(<I18nProvider><PwaInstallProvider><PwaInstall /></PwaInstallProvider></I18nProvider>);
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Install Meander' })); });
  expect(prompt).toHaveBeenCalledOnce();
});

it('offers manual iOS instructions, but no native install button or advice in standalone mode', () => {
  vi.stubGlobal('navigator', { userAgent: 'iPhone', standalone: false, onLine: true });
  const { unmount } = render(<I18nProvider><PwaInstallProvider><PwaInstall /></PwaInstallProvider></I18nProvider>);
  expect(screen.getByText(/Share → Add to Home Screen/)).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Install Meander' })).not.toBeInTheDocument();
  unmount(); vi.stubGlobal('navigator', { userAgent: 'iPhone', standalone: true, onLine: true });
  render(<I18nProvider><PwaInstallProvider><PwaInstall /></PwaInstallProvider></I18nProvider>);
  expect(screen.queryByText(/Share → Add to Home Screen/)).not.toBeInTheDocument();
});

it('never registers a worker in development or an unsupported browser', async () => {
  const register = vi.fn();
  vi.stubGlobal('navigator', { serviceWorker: { register } });
  expect(await registerShell(false)).toBeNull();
  expect(register).not.toHaveBeenCalled();
});

it('does not offer a first installation as an update, but offers a genuinely waiting replacement', async () => {
  const module = await import('./pwa');
  const worker = new EventTarget();
  const registration = Object.assign(new EventTarget(), { waiting: worker, installing: null });
  const serviceWorker = Object.assign(new EventTarget(), { controller: null as EventTarget | null });
  vi.stubGlobal('navigator', { serviceWorker, userAgent: '', onLine: true });
  vi.spyOn(module, 'registerShell').mockResolvedValue(registration as unknown as ServiceWorkerRegistration);
  let unmount = () => {};
  await act(async () => { ({ unmount } = render(<I18nProvider><PwaStatus /></I18nProvider>)); });
  expect(screen.queryByRole('button', { name: 'Update and reload' })).not.toBeInTheDocument();
  unmount(); serviceWorker.controller = new EventTarget();
  await act(async () => { render(<I18nProvider><PwaStatus /></I18nProvider>); });
  expect(screen.getByRole('button', { name: 'Update and reload' })).toBeInTheDocument();
});

it('only reloads the consenting tab after its waiting worker takes control', () => {
  const events = new EventTarget();
  vi.stubGlobal('navigator', { serviceWorker: events });
  const worker = { postMessage: vi.fn() } as unknown as ServiceWorker;
  const reload = vi.fn();
  events.dispatchEvent(new Event('controllerchange'));
  expect(reload).not.toHaveBeenCalled();
  activateUpdate(worker, reload);
  expect(worker.postMessage).toHaveBeenCalledWith({ type: 'ACTIVATE_UPDATE' });
  events.dispatchEvent(new Event('controllerchange'));
  events.dispatchEvent(new Event('controllerchange'));
  expect(reload).toHaveBeenCalledOnce();
});
