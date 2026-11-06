/* Generated for one immutable build. Never runtime-cache API, routes, map data or third parties. */
const CACHE = 'meander-shell-__VERSION__';
const SHELL = __PRECACHE__;
const publicAssets = new Set(SHELL);

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).catch(async error => {
    await caches.delete(CACHE);
    throw error;
  }));
  // The browser keeps an update waiting until consent, or until all old tabs close.
});
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    const previous = (await caches.keys()).filter(key => key.startsWith('meander-shell-') && key !== CACHE);
    // Keep one prior immutable shell for a still-open tab; never touch another app's caches.
    await Promise.all(previous.slice(0, -1).map(key => caches.delete(key)));
    await self.clients.claim();
  })());
});
self.addEventListener('message', event => {
  if (event.data?.type === 'ACTIVATE_UPDATE') self.skipWaiting();
});

self.addEventListener('fetch', event => {
  const { request } = event;
  const url = new URL(request.url);
  if (request.method !== 'GET' || url.origin !== self.location.origin) return;
  // Only these explicit SPA paths return the generic, non-personalized shell.
  // Do not cache navigation URLs: saved-route IDs and OAuth query strings are private.
  if (request.mode === 'navigate' && (url.pathname === '/' || /^\/routes\/[^/]+\/?$/.test(url.pathname))) {
    event.respondWith(caches.open(CACHE).then(cache => cache.match('/index.html')));
  } else if (!url.search && (publicAssets.has(url.pathname) || /^\/assets\/[^/]+-[\w-]+\.(?:js|css|woff2?)$/.test(url.pathname))) {
    event.respondWith((async () => {
      const current = await (await caches.open(CACHE)).match(url.pathname);
      if (current) return current;
      // Old tabs may still request an immutable chunk from the retained previous build.
      // Match only our caches; never admit arbitrary requests into Cache Storage.
      const previous = (await caches.keys()).filter(key => key.startsWith('meander-shell-') && key !== CACHE);
      for (const name of previous.reverse()) {
        const cached = await (await caches.open(name)).match(url.pathname);
        if (cached) return cached;
      }
      return fetch(request);
    })());
  }
});
