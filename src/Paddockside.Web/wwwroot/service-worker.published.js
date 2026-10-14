// The published service worker (owner PWA shell; https://aka.ms/blazor-offline-considerations).
//
// Network first: when online, every request goes to the server as usual (the browser's own HTTP cache still
// revalidates cheaply), so a new release is what people get on their next load. The offline cache is only a
// fallback when the network fails. A new version takes over as soon as it is installed rather than waiting for
// every tab to close. (Blazor's template served everything from the cache and waited, so after each deploy staff and
// owners kept seeing the old pages until a hard refresh: 14 Oct 2026.)

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest, for offline use only.
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));

    // Take over now, not when every tab has been closed.
    await self.skipWaiting();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches, then control open pages straight away.
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    await self.clients.claim();
}

async function onFetch(event) {
    try {
        return await fetch(event.request);
    } catch (error) {
        // Offline: serve what we have. Navigations get the app's index.html; anything else, its cached copy.
        if (event.request.method !== 'GET') throw error;
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);
        const cache = await caches.open(cacheName);
        const cached = await cache.match(shouldServeIndexHtml ? 'index.html' : event.request);
        if (cached) return cached;
        throw error;
    }
}
