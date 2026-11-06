import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { expect, it, vi } from 'vitest';
function worker() {
  const listeners: Record<string, (event: any) => void> = {};
  const cache = { addAll: vi.fn().mockResolvedValue(undefined), match: vi.fn().mockResolvedValue(new Response('shell')) };
  const caches = { open: vi.fn().mockResolvedValue(cache), keys: vi.fn().mockResolvedValue(['other-app', 'meander-shell-v1', 'meander-shell-v2', 'meander-shell-v3']), delete: vi.fn().mockResolvedValue(true) };
  const self = { location: { origin: 'https://meander.test' }, clients: { claim: vi.fn() }, skipWaiting: vi.fn(), addEventListener: (name: string, fn: any) => { listeners[name] = fn; } };
  const code = readFileSync('pwa/sw.js', 'utf8').replace('__PRECACHE__', JSON.stringify(['/index.html', '/assets/app-abc.js'])).replace('__VERSION__', 'v3');
  vm.runInNewContext(code, { self, caches, URL, fetch: vi.fn(), Response });
  return { listeners, cache, caches, self };
}
it('only handles the public shell: private/API/map/external/unknown URLs and POST stay network-only', () => {
  const { listeners } = worker();
  for (const [path, method, mode] of [
    ['/api/connections', 'GET', 'navigate'], ['/api/searches/private', 'GET', 'cors'], ['/api/routes/generate', 'POST', 'cors'],
    ['/map/netherlands.pmtiles', 'GET', 'cors'], ['https://tile.openstreetmap.org/1/2/3.png', 'GET', 'cors'],
    ['/assets/unknown.js', 'GET', 'cors'], ['/assets/app-abc.js?token=private', 'GET', 'cors'], ['/auth/callback', 'GET', 'navigate'],
  ]) {
    const respondWith = vi.fn();
    listeners.fetch({ request: { url: new URL(path, 'https://meander.test').href, method, mode }, respondWith });
    expect(respondWith, path).not.toHaveBeenCalled();
  }
});

it('atomically precaches only this build, waits for explicit update consent and deletes only older Meander caches', async () => {
  const { listeners, cache, caches, self } = worker();
  let pending: Promise<unknown> = Promise.resolve();
  const event = { waitUntil: (p: Promise<unknown>) => { pending = p; } };
  expect(listeners.install).toBeTypeOf('function');
  listeners.install(event); await pending;
  expect(cache.addAll).toHaveBeenCalledWith(['/index.html', '/assets/app-abc.js']);
  expect(self.skipWaiting).not.toHaveBeenCalled();
  listeners.activate(event); await pending;
  expect(caches.delete).toHaveBeenCalledExactlyOnceWith('meander-shell-v1');
  expect(self.clients.claim).toHaveBeenCalledOnce();
  listeners.message({ data: { type: 'IGNORE' } });
  expect(self.skipWaiting).not.toHaveBeenCalled();
  listeners.message({ data: { type: 'ACTIVATE_UPDATE' } });
  expect(self.skipWaiting).toHaveBeenCalledOnce();
});

it('does not activate an incomplete shell and removes its partial cache', async () => {
  const { listeners, cache, caches } = worker();
  cache.addAll.mockRejectedValueOnce(new Error('offline asset'));
  let pending: Promise<unknown> = Promise.resolve();
  listeners.install({ waitUntil: (p: Promise<unknown>) => { pending = p; } });
  await expect(pending).rejects.toThrow('offline asset');
  expect(caches.delete).toHaveBeenCalledWith('meander-shell-v3');
});

it('can serve a retained immutable chunk for an older open tab, without looking in another app cache', async () => {
  const { listeners, cache, caches } = worker();
  cache.match.mockResolvedValueOnce(undefined).mockResolvedValueOnce(new Response('old immutable chunk'));
  const respondWith = vi.fn();
  listeners.fetch({ request: { url: 'https://meander.test/assets/app-oldhash.js', mode: 'cors', method: 'GET' }, respondWith });
  expect(respondWith).toHaveBeenCalledOnce();
  expect(await (await respondWith.mock.calls[0][0]).text()).toBe('old immutable chunk');
  expect(caches.open).not.toHaveBeenCalledWith('other-app');
});

it('serves the pinned shell on saved-route navigation, without caching route content or navigation URLs', async () => {
  const { listeners, cache } = worker();
  const respondWith = vi.fn();
  listeners.fetch({ request: { url: 'https://meander.test/routes/private?route=secret', mode: 'navigate', method: 'GET' }, respondWith });
  expect(await respondWith.mock.calls[0][0]).toBeInstanceOf(Response);
  expect(cache.match).toHaveBeenCalledExactlyOnceWith('/index.html');
});
