/* MatRegistry service worker: the app as a PWA – small on purpose.
   - Static files (toolkit, styles, scripts, icons): from the cache first, refreshed in the background.
   - The pages to look at (overview, repositories with their tags and manifests, registries, copies) come from the network; a copy of the last ones is kept, so
     without a network what was seen last is still shown (read-only: every change needs the network). Other pages without a network: the offline page with a
     button to try again.
   - The registry API (/v2/), the platform API, the search suggestions and every change (POST) are never touched. */
const VERSION = 'matregistry-v1';
const PAGES = 'mr-pages';
const SHELL = [
  '/offline.html', '/js/offline.js', '/js/pwa.js', '/js/util.js', '/matui/matui.css', '/matui/matui.js', '/matui/matui.boot.js', '/css/app.css', '/favicon.svg',
  '/img/icon-192.png', '/img/icon-512.png', '/img/logo/matregistry-logo.svg', '/img/logo/matregistry-logo-dark.svg', '/img/logo/matregistry-mark.svg', '/manifest.webmanifest',
];
const MAX_PAGES = 80;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// signing out clears what was kept for this person (sent by pwa.js)
self.addEventListener('message', event => {
  if (event.data === 'md-clear') {
    event.waitUntil(caches.delete(PAGES));
  }
});

const isStatic = url => /^\/(matui|css|js|img)\//.test(url.pathname) || ['/favicon.svg', '/manifest.webmanifest', '/offline.html'].includes(url.pathname);
const isReadPage = url => /^\/(Overview|Repositories|Repositories\/View|Repositories\/Manifest|Registries|Registries\/View|Copies|Copies\/View|Account\/Tokens)$/i.test(url.pathname);

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? Promise.all(keys.slice(0, keys.length - max).map(k => cache.delete(k))) : undefined));
}

function networkFirst(request, cacheName, max, fallback) {
  return fetch(request).then(response => {
    if (response.ok) {
      const copy = response.clone();
      caches.open(cacheName).then(cache => cache.put(request, copy).then(() => trim(cache, max)));
    }
    return response;
  }).catch(() => caches.open(cacheName).then(cache => cache.match(request)).then(hit => hit || (fallback ? caches.match(fallback) : Response.error())));
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health') return;
  if (/^\/(api|live|v2)(\/|$)/.test(url.pathname)) return;

  if (request.mode === 'navigate') {
    event.respondWith(isReadPage(url) ? networkFirst(request, PAGES, MAX_PAGES, '/offline.html') : fetch(request).catch(() => caches.match('/offline.html')));
    return;
  }

  // scripts and styles without a version in the address: from the network while there is one, so a new build is used at once
  if (isStatic(url) && !url.search.includes('v=') && /^\/(js|css)\//.test(url.pathname)) { event.respondWith(networkFirst(request, VERSION, 500)); return; }

  if (isStatic(url)) {
    event.respondWith(caches.open(VERSION).then(cache => cache.match(request).then(hit => {
      const fresh = fetch(request).then(response => { if (response.ok) cache.put(request, response.clone()); return response; }).catch(() => hit);
      return hit || fresh;
    })));
  }
});
