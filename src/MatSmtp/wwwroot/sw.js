/* MatSMTP service worker: makes the app installable and usable without a connection, as far as that makes sense for a relay (reading the last state).
   - The files of the app (toolkit, styles, scripts, icons) come from the cache first: their addresses carry a version, so a new build loads new files.
   - Pages come from the network first; a page that loaded is kept, so the last state of a list or a message can be read offline. Such a copy is marked
     (data-offline-copy on <html>), and app.js says so and reloads it as soon as the connection is back: nothing stale stays on the screen.
   - Nothing that changes data is ever answered from the cache (POST, /api), nor sign-in, share links or downloads. Without a copy the offline page explains it.
   - Signing out (or the sign-in page) clears the kept pages: they hold the data of the person who was signed in.
   The version comes from the address the layout registers (sw.js?v=...): a new build is a new worker, which removes the caches of the old one. */
'use strict';

var VERSION = new URL(self.location.href).searchParams.get('v') || 'dev';
var STATIC = 'ms-static-' + VERSION;
var PAGES = 'ms-pages';
var OFFLINE = '/Offline';
var MAX_PAGES = 60;
var NETWORK_TIMEOUT = 6000;

self.addEventListener('install', function (event) {
  event.waitUntil(
    caches.open(STATIC)
      .then(function (cache) { return cache.addAll([OFFLINE, '/favicon.svg', '/img/icon-192.png', '/manifest.webmanifest']); })
      .then(function () { return self.skipWaiting(); })
  );
});

self.addEventListener('activate', function (event) {
  event.waitUntil(
    caches.keys().then(function (keys) {
      return Promise.all(keys.filter(function (k) { return k.indexOf('ms-static-') === 0 && k !== STATIC; }).map(function (k) { return caches.delete(k); }));
    }).then(function () { return self.clients.claim(); })
  );
});

self.addEventListener('message', function (event) {
  var data = event.data || {};
  if (data.type === 'clear-pages') {
    event.waitUntil(caches.delete(PAGES));
  }
});

function isStatic(url) {
  return /^\/(ui|css|js|img)\//.test(url.pathname) || /\.(svg|png|woff2?)$/.test(url.pathname) || url.pathname === '/manifest.webmanifest';
}

function isPrivateOrLive(url) {
  var p = url.pathname;
  return /^\/(api|account|s|health|messages)(\/|$)/i.test(p) || p === '/sw.js';
}

self.addEventListener('fetch', function (event) {
  var request = event.request;
  if (request.method !== 'GET') { return; }
  var url = new URL(request.url);
  if (url.origin !== self.location.origin) { return; }

  if (isStatic(url)) {
    event.respondWith(caches.open(STATIC).then(function (cache) {
      return cache.match(request).then(function (hit) {
        return hit || fetch(request).then(function (response) {
          if (response.ok) { cache.put(request, response.clone()); }
          return response;
        });
      });
    }));
    return;
  }

  if (request.mode !== 'navigate' || isPrivateOrLive(url)) { return; }
  event.respondWith(networkFirst(request));
});

function timeout(ms) {
  return new Promise(function (_, reject) { setTimeout(function () { reject(new Error('timeout')); }, ms); });
}

function networkFirst(request) {
  return Promise.race([fetch(request), timeout(NETWORK_TIMEOUT)]).then(function (response) {
    // Keep a copy of a page that loaded normally (not a redirect to the sign-in, not an error page).
    if (response.ok && !response.redirected && (response.headers.get('content-type') || '').indexOf('text/html') === 0) {
      var copy = response.clone();
      caches.open(PAGES).then(function (cache) { return cache.put(request, copy).then(function () { return trim(cache); }); });
    }
    return response;
  }).catch(function () {
    return caches.open(PAGES).then(function (cache) { return cache.match(request, { ignoreVary: true }); }).then(function (hit) {
      if (!hit) { return caches.match(OFFLINE); }
      return markAsCopy(hit);
    });
  });
}

/* The kept page says that it is a copy, and from when: app.js shows it and reloads when the connection is back. */
function markAsCopy(response) {
  var when = response.headers.get('date') || '';
  return response.text().then(function (html) {
    var marked = html.replace('<html ', '<html data-offline-copy="' + when.replace(/"/g, '') + '" ');
    return new Response(marked, { status: 200, headers: { 'Content-Type': 'text/html; charset=utf-8' } });
  });
}

function trim(cache) {
  return cache.keys().then(function (keys) {
    if (keys.length <= MAX_PAGES) { return; }
    return Promise.all(keys.slice(0, keys.length - MAX_PAGES).map(function (k) { return cache.delete(k); }));
  });
}
