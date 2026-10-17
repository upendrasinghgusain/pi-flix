// Makes the app installable, and shows release alerts sent by the Pi.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));

self.addEventListener("push", (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { body: event.data?.text() }; }
  event.waitUntil(self.registration.showNotification(data.title || "Watchlist", {
    body: data.body || "",
    tag: data.tag,
    renotify: Boolean(data.tag),
    icon: "icon.svg",
    badge: "icon.svg",
    data: { url: new URL(data.url || "./", self.registration.scope).href },
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = event.notification.data?.url || self.registration.scope;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of windows) {
      if (client.url.startsWith(self.registration.scope)) {
        await client.focus();
        client.postMessage({ url });
        return;
      }
    }
    await self.clients.openWindow(url);
  })());
});
