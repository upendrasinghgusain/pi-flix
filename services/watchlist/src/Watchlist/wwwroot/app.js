"use strict";

const IMG = "https://image.tmdb.org/t/p/";
const regionNames = new Intl.DisplayNames(["en-GB"], { type: "region" });
const dateFmt = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const CATEGORY = { flatrate: "stream", free: "free", ads: "free with ads", rent: "rent", buy: "buy" };
const KIND = { digital: "digital", disc: "disc", tv: "on TV", aired: "first aired", cinema: "cinemas" };
const STATUS = { waiting: "waiting", available: "available", in_library: "in library", watched: "watched", removed: "removed" };
const DOWNLOAD = { pending: "sending to Radarr", searching: "looking for a copy", downloading: "downloading" };

const $ = (id) => document.getElementById(id);

/** Builds an element. Text is always set as text, never as HTML. */
function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value == null || value === false) continue;
    if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat()) {
    if (child == null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

async function api(path, options = {}) {
  const init = { ...options, headers: { ...(options.headers || {}) } };
  if (init.json !== undefined) {
    init.body = JSON.stringify(init.json);
    init.headers["Content-Type"] = "application/json";
    delete init.json;
  }
  const response = await fetch(path, init);
  if (response.status === 403) {
    // The Cloudflare login has expired: reload to go through it again.
    location.reload();
    throw new Error("Signed out");
  }
  const body = response.headers.get("content-type")?.includes("json") ? await response.json() : null;
  if (!response.ok) throw new Error(body?.error || body?.detail || `Request failed (${response.status})`);
  return body;
}

const country = (code) => {
  if (!code) return "somewhere";
  try { return regionNames.of(code) || code; } catch { return code; }
};
const fmtDate = (iso) => dateFmt.format(new Date(iso + "T00:00:00"));
const today = () => new Date().toISOString().slice(0, 10);
const label = (item) => (item.year ? `${item.title} (${item.year})` : item.title);
const typeName = (t) => (t === "tv" ? "TV" : "Film");

function poster(path, size) {
  return path
    ? el("img", { src: `${IMG}${size}${path}`, alt: "", loading: "lazy" })
    : el("div", { class: "noposter", "aria-hidden": "true" });
}

// ---------- Tabs ----------

function show(view) {
  for (const tab of document.querySelectorAll(".tabs button")) {
    tab.setAttribute("aria-selected", String(tab.dataset.view === view));
  }
  $("view-chat").hidden = view !== "chat";
  $("view-list").hidden = view !== "list";
  if (view === "list") loadItems();
  else $("input").focus({ preventScroll: true });
}

for (const tab of document.querySelectorAll(".tabs button")) {
  tab.addEventListener("click", () => {
    history.replaceState(null, "", tab.dataset.view === "list" ? "#list" : "#");
    show(tab.dataset.view);
  });
}

// ---------- Toast ----------

let toastTimer;
function toast(text, action) {
  const box = $("toast");
  box.replaceChildren(el("span", {}, text));
  if (action) {
    box.append(el("button", { type: "button", onclick: () => { box.hidden = true; action.run(); } }, action.label));
  }
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, action ? 6000 : 3000);
}

// ---------- Chat ----------

const messages = $("messages");
const input = $("input");
let lastUserText = "";
// Each opening of the app is a fresh conversation; the agent only remembers this one.
const conversation = crypto.randomUUID();

function scrollChat() {
  messages.scrollTop = messages.scrollHeight;
}

function bubble(role, text) {
  const node = el("div", { class: `bubble ${role}` }, text);
  messages.append(node);
  scrollChat();
  return node;
}

function miniCard(item, extra) {
  return el("div", { class: "mini" },
    poster(item.posterPath, "w92"),
    el("div", { class: "meta" },
      el("strong", {}, item.title),
      el("span", {}, [typeName(item.mediaType), item.year].filter(Boolean).join(" · "))),
    extra);
}

function showAdded(items) {
  if (!items?.length) return;
  const box = el("div", { class: "attachments" });
  for (const item of items) {
    const undo = el("button", {
      type: "button", class: "undo",
      onclick: async () => {
        undo.disabled = true;
        try {
          await api(`/api/items/${item.id}`, { method: "PATCH", json: { status: "removed" } });
          undo.replaceWith(el("span", { class: "small" }, "Undone"));
        } catch (e) { undo.disabled = false; toast(e.message); }
      },
    }, "Undo");
    box.append(miniCard(item, undo));
  }
  messages.append(box);
  scrollChat();
}

function showChoices(choice) {
  if (!choice?.options?.length) return;
  const box = el("div", { class: "attachments" });
  const addedText = lastUserText;
  for (const option of choice.options) {
    const card = el("button", {
      type: "button", class: "mini choice",
      onclick: async () => {
        for (const b of box.querySelectorAll("button")) b.disabled = true;
        try {
          const result = await api("/api/choose", {
            method: "POST",
            json: { tmdbId: option.tmdbId, mediaType: option.mediaType, addedText, conversation },
          });
          box.remove();
          bubble("assistant", result.reply);
          showAdded(result.added);
        } catch (e) {
          for (const b of box.querySelectorAll("button")) b.disabled = false;
          toast(e.message);
        }
      },
    },
      poster(option.posterPath, "w92"),
      el("div", { class: "meta" },
        el("strong", {}, option.title),
        el("span", {}, [typeName(option.mediaType), option.year].filter(Boolean).join(" · "))));
    box.append(card);
  }
  messages.append(box);
  scrollChat();
}

async function send(text) {
  text = text.trim();
  if (!text) return;
  lastUserText = text;
  bubble("user", text);
  input.value = "";
  autosize();
  const pending = bubble("assistant pending", "Thinking");
  $("send").disabled = true;
  try {
    const result = await api("/api/chat", { method: "POST", json: { text, conversation } });
    pending.remove();
    bubble("assistant", result.reply);
    showAdded(result.added);
    showChoices(result.choice);
  } catch (e) {
    pending.remove();
    bubble("assistant", `Sorry, that didn't work: ${e.message}`);
  } finally {
    $("send").disabled = false;
  }
}

function greet() {
  bubble("assistant", "What do you want to watch? Even roughly: \"I want to watch that film where Matt Damon grows potatoes\". Films are downloaded for you. Or ask where something's showing.");
}

function autosize() {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, 140) + "px";
}

input.addEventListener("input", autosize);
input.addEventListener("keydown", (e) => {
  // Enter sends on a keyboard; on a phone the send button does.
  if (e.key === "Enter" && !e.shiftKey && !matchMedia("(pointer: coarse)").matches) {
    e.preventDefault();
    send(input.value);
  }
});
$("composer").addEventListener("submit", (e) => { e.preventDefault(); send(input.value); });

// ---------- Voice: record, transcribe, then edit before sending ----------

const mic = $("mic");
let recorder = null;
let chunks = [];

function pickMime() {
  for (const type of ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg"]) {
    if (window.MediaRecorder && MediaRecorder.isTypeSupported(type)) return type;
  }
  return "";
}

async function startRecording() {
  if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) {
    toast("This browser can't record audio.");
    return;
  }
  let stream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
  } catch {
    toast("Microphone access was refused.");
    return;
  }
  const mimeType = pickMime();
  recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
  chunks = [];
  recorder.addEventListener("dataavailable", (e) => { if (e.data.size) chunks.push(e.data); });
  recorder.addEventListener("stop", async () => {
    stream.getTracks().forEach((t) => t.stop());
    mic.classList.remove("recording");
    const type = recorder.mimeType || "audio/webm";
    recorder = null;
    if (!chunks.length) return;
    const blob = new Blob(chunks, { type });
    const ext = type.includes("mp4") ? "m4a" : type.includes("ogg") ? "ogg" : "webm";
    const form = new FormData();
    form.append("audio", blob, `voice.${ext}`);
    mic.classList.add("busy");
    mic.disabled = true;
    try {
      const result = await api("/api/transcribe", { method: "POST", body: form });
      input.value = input.value ? `${input.value} ${result.text}` : result.text;
      autosize();
      input.focus();
    } catch (e) {
      toast(e.message);
    } finally {
      mic.classList.remove("busy");
      mic.disabled = false;
    }
  });
  recorder.start();
  mic.classList.add("recording");
  mic.setAttribute("aria-label", "Stop recording");
}

function stopRecording() {
  if (recorder?.state === "recording") recorder.stop();
  mic.setAttribute("aria-label", "Record a voice message");
}

// Tap to start and tap to stop, or hold and release.
let pressStart = 0;
mic.addEventListener("pointerdown", (e) => {
  e.preventDefault();
  if (recorder) { stopRecording(); return; }
  pressStart = Date.now();
  startRecording();
});
mic.addEventListener("pointerup", () => {
  if (recorder && Date.now() - pressStart > 600) stopRecording();
});

// ---------- List ----------

let items = [];
let filter = "active";

function isSnoozed(item) {
  return item.snoozedUntil && item.snoozedUntil > today();
}

function visible(item) {
  const q = $("filter-text").value.trim().toLowerCase();
  if (q && !item.title.toLowerCase().includes(q) && !(item.addedText || "").toLowerCase().includes(q)) return false;
  if (filter === "snoozed") return isSnoozed(item);
  if (filter === "watched") return item.status === "watched";
  if (isSnoozed(item)) return false;
  if (filter === "active") return item.status !== "watched";
  return item.status === filter;
}

function releaseLine(release, upcomingVerb) {
  const where = release.country === "GB" ? "in the UK" : `in ${country(release.country)}`;
  const kind = KIND[release.kind] || release.kind;
  const past = release.date <= today();
  return past
    ? `${kind[0].toUpperCase()}${kind.slice(1)} ${where} since ${fmtDate(release.date)}`
    : `${upcomingVerb}: ${kind} ${where} on ${fmtDate(release.date)}`;
}

function offerChips(offers) {
  return el("div", { class: "offers" },
    offers.map((o) => el("span", { class: "offer" }, o.provider, " ", el("span", { class: "cat" }, CATEGORY[o.category] || o.category))));
}

function facts(item) {
  const a = item.availability;
  if (!a) return el("p", { class: "small" }, "Not checked yet.");
  const list = el("ul", { class: "facts" });

  if (item.mediaType === "tv") {
    if (a.nextEpisode?.airDate) {
      const ep = a.nextEpisode;
      list.append(el("li", { class: "lead" }, `Next: S${ep.season}E${ep.episode} on ${fmtDate(ep.airDate)}`));
    }
    if (a.firstHome) list.append(el("li", {}, releaseLine(a.firstHome, "Starts")));
    if (a.showStatus) list.append(el("li", { class: "small" }, `${a.showStatus}${a.numberOfSeasons ? ` · ${a.numberOfSeasons} season${a.numberOfSeasons === 1 ? "" : "s"}` : ""}`));
  } else {
    if (a.firstHome) list.append(el("li", { class: "lead" }, releaseLine(a.firstHome, "Coming")));
    else if (!a.isAvailable) list.append(el("li", { class: "lead" }, "No home release date yet"));
    if (a.ukHome && (!a.firstHome || a.ukHome.country !== a.firstHome.country || a.ukHome.date !== a.firstHome.date)) {
      list.append(el("li", {}, releaseLine(a.ukHome, "UK")));
    }
    const cinema = a.ukCinema || a.firstCinema;
    if (cinema && !a.isAvailable) list.append(el("li", {}, releaseLine(cinema, "Cinemas")));
  }

  if (a.uk?.length) {
    list.append(el("li", {}, el("span", { class: "small" }, "In the UK"), offerChips(a.uk)));
  } else if (a.isAvailable) {
    list.append(el("li", { class: "small" }, "Nothing in the UK yet"));
  }

  if (a.elsewhere?.length) {
    list.append(el("li", {},
      el("details", { class: "elsewhere" },
        el("summary", {}, `Also in ${a.elsewhere.length} other ${a.elsewhere.length === 1 ? "country" : "countries"}`),
        el("ul", {}, a.elsewhere.map((c) =>
          el("li", {}, `${country(c.country)}: ${c.offers.map((o) => `${o.provider} (${CATEGORY[o.category] || o.category})`).join(", ")}`))))));
  }
  return list;
}

function card(item) {
  const status = isSnoozed(item) ? "snoozed" : item.status;
  const added = new Date(item.addedAt);
  const checked = item.lastCheckedAt ? new Date(item.lastCheckedAt) : null;

  const actions = el("div", { class: "actions" });
  if (item.status !== "watched") actions.append(el("button", { type: "button", onclick: () => setStatus(item, "watched", "Marked watched") }, "Watched"));
  else actions.append(el("button", { type: "button", onclick: () => setStatus(item, "waiting", "Back on the list") }, "Not watched"));
  actions.append(
    el("button", { type: "button", onclick: () => openMatch(item) }, "Wrong title"),
    el("button", { type: "button", onclick: () => checkNow(item) }, "Check now"),
    el("button", { type: "button", onclick: () => openNotes(item) }, "Notes"),
    isSnoozed(item)
      ? el("button", { type: "button", onclick: () => patch(item, { clearSnooze: true }, "Unsnoozed") }, "Unsnooze")
      : el("button", { type: "button", onclick: () => openSnooze(item) }, "Snooze"),
    el("button", { type: "button", class: "danger", onclick: () => remove(item) }, "Delete"));

  return el("article", { class: "card", id: `item-${item.id}` },
    poster(item.posterPath, "w185"),
    el("div", {},
      el("h3", {}, item.title, item.year ? el("span", { class: "year" }, ` (${item.year})`) : null),
      el("div", { class: "tags" },
        el("span", { class: "pill type" }, typeName(item.mediaType)),
        el("span", { class: `pill ${status}` }, status === "snoozed" ? `snoozed to ${fmtDate(item.snoozedUntil)}` : STATUS[status] || status),
        DOWNLOAD[item.downloadState] ? el("span", { class: `pill download ${item.downloadState}` }, DOWNLOAD[item.downloadState]) : null),
      facts(item),
      item.notes ? el("p", { class: "note" }, item.notes) : null,
      el("p", { class: "small" },
        item.addedText ? `“${item.addedText}” · ` : "",
        `added ${dateFmt.format(added)}`,
        checked ? ` · checked ${dateFmt.format(checked)}` : "")),
    actions);
}

function renderItems() {
  const shown = items.filter(visible);
  $("cards").replaceChildren(...shown.map(card));
  $("empty").hidden = shown.length > 0;
  for (const chip of document.querySelectorAll(".chip")) {
    chip.setAttribute("aria-pressed", String(chip.dataset.filter === filter));
  }
}

async function loadItems() {
  try {
    items = await api("/api/items");
    renderItems();
    const target = location.hash.match(/^#item-(\d+)$/);
    if (target) {
      const node = $(`item-${target[1]}`);
      node?.scrollIntoView({ block: "center" });
      node?.classList.add("flash");
    }
  } catch (e) {
    toast(e.message);
  }
}

function replaceItem(updated) {
  items = items.map((i) => (i.id === updated.id ? updated : i));
  renderItems();
}

async function patch(item, body, message) {
  try {
    replaceItem(await api(`/api/items/${item.id}`, { method: "PATCH", json: body }));
    if (message) toast(message);
  } catch (e) { toast(e.message); }
}

function setStatus(item, status, message) {
  const previous = item.status;
  patch(item, { status }, null).then(() =>
    toast(message, { label: "Undo", run: () => patch(item, { status: previous }, null) }));
}

async function remove(item) {
  const previous = item.status;
  try {
    await api(`/api/items/${item.id}`, { method: "PATCH", json: { status: "removed" } });
    items = items.filter((i) => i.id !== item.id);
    renderItems();
    toast(`Deleted ${label(item)}`, {
      label: "Undo",
      run: async () => {
        try {
          const restored = await api(`/api/items/${item.id}`, { method: "PATCH", json: { status: previous } });
          items = [restored, ...items].sort((a, b) => b.addedAt.localeCompare(a.addedAt));
          renderItems();
        } catch (e) { toast(e.message); }
      },
    });
  } catch (e) { toast(e.message); }
}

async function checkNow(item) {
  toast(`Checking ${label(item)}…`);
  try {
    const result = await api(`/api/items/${item.id}/check`, { method: "POST" });
    replaceItem(result.item);
    toast(result.alerts.length ? result.alerts.join(" · ") : "Up to date, nothing new");
  } catch (e) { toast(e.message); }
}

for (const chip of document.querySelectorAll(".chip")) {
  chip.addEventListener("click", () => { filter = chip.dataset.filter; renderItems(); });
}
$("filter-text").addEventListener("input", renderItems);

// ---------- Dialogs ----------

let editing = null;

function openNotes(item) {
  editing = item;
  $("notes-text").value = item.notes || "";
  $("notes-dialog").showModal();
}

$("notes-dialog").addEventListener("close", () => {
  if ($("notes-dialog").returnValue === "save" && editing) patch(editing, { notes: $("notes-text").value }, "Saved");
});

function openSnooze(item) {
  editing = item;
  const week = new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10);
  $("snooze-date").min = today();
  $("snooze-date").value = week;
  $("snooze-dialog").showModal();
}

$("snooze-dialog").addEventListener("close", () => {
  const date = $("snooze-date").value;
  if ($("snooze-dialog").returnValue === "save" && editing && date) patch(editing, { snoozedUntil: date }, `Snoozed until ${fmtDate(date)}`);
});

let searchTimer;
function openMatch(item) {
  editing = item;
  $("match-query").value = item.addedText || item.title;
  $("match-results").replaceChildren();
  $("match-dialog").showModal();
  runMatchSearch();
}

async function runMatchSearch() {
  const q = $("match-query").value.trim();
  if (!q) return;
  try {
    const results = await api(`/api/search?q=${encodeURIComponent(q)}`);
    $("match-results").replaceChildren(...results.map((r) =>
      el("li", {}, el("button", {
        type: "button", class: "mini choice",
        onclick: async () => {
          const target = editing;
          $("match-dialog").close();
          try {
            replaceItem(await api(`/api/items/${target.id}/match`, { method: "PUT", json: { tmdbId: r.tmdbId, mediaType: r.mediaType } }));
            toast(`Changed to ${label(r)}`);
          } catch (e) { toast(e.message); }
        },
      },
        poster(r.posterPath, "w92"),
        el("div", { class: "meta" }, el("strong", {}, r.title), el("span", {}, [typeName(r.mediaType), r.year].filter(Boolean).join(" · ")))))));
    if (!results.length) $("match-results").append(el("li", { class: "small" }, "No matches."));
  } catch (e) { toast(e.message); }
}

$("match-query").addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(runMatchSearch, 300);
});
$("match-form").addEventListener("submit", (e) => {
  if (e.submitter?.value !== "cancel") { e.preventDefault(); runMatchSearch(); }
});

// ---------- Alerts (Web Push) ----------

const bell = $("bell");
const pushSupported = "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

function setBell(on) {
  bell.setAttribute("aria-pressed", String(on));
  bell.setAttribute("aria-label", on ? "Alerts are on" : "Turn on alerts");
  bell.title = on ? "Alerts are on" : "Turn on alerts";
}

function keyBytes(base64url) {
  const b64 = base64url.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(base64url.length / 4) * 4, "=");
  return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
}

async function currentSubscription() {
  const registration = await navigator.serviceWorker.ready;
  return registration.pushManager.getSubscription();
}

async function turnAlertsOn() {
  if (await Notification.requestPermission() !== "granted") {
    toast("Notifications are blocked. Allow them in the browser's site settings.");
    return;
  }
  const { publicKey } = await api("/api/push/key");
  const registration = await navigator.serviceWorker.ready;
  let subscription = await registration.pushManager.getSubscription();
  if (!subscription) {
    subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
  }
  await api("/api/push/subscribe", { method: "POST", json: subscription.toJSON() });
  setBell(true);
  const result = await api("/api/push/test", { method: "POST" });
  toast(result.sent ? "Alerts are on. A test alert is on its way." : "Alerts are on, but the test didn't arrive.");
}

async function turnAlertsOff() {
  const subscription = await currentSubscription();
  if (subscription) {
    await api("/api/push/unsubscribe", { method: "POST", json: { endpoint: subscription.endpoint } });
    await subscription.unsubscribe();
  }
  setBell(false);
  toast("Alerts are off on this device.");
}

bell.addEventListener("click", async () => {
  if (!pushSupported) {
    toast("This browser can't receive alerts. On a phone, install the app first.");
    return;
  }
  try {
    if (bell.getAttribute("aria-pressed") === "true") {
      toast("Alerts are on for this device.", { label: "Turn off", run: () => turnAlertsOff().catch((e) => toast(e.message)) });
    } else {
      await turnAlertsOn();
    }
  } catch (e) {
    toast(e.message);
  }
});

// ---------- Start ----------

if ("serviceWorker" in navigator) {
  navigator.serviceWorker.register("sw.js").catch(() => {});
  if (pushSupported && Notification.permission === "granted") {
    currentSubscription().then((s) => setBell(Boolean(s))).catch(() => {});
  }
}
// Tapping an alert while the app is open moves to that title.
navigator.serviceWorker?.addEventListener("message", (e) => {
  if (e.data?.url) { location.hash = new URL(e.data.url, location.href).hash; show("list"); }
});

greet();
show(location.hash.startsWith("#item-") || location.hash === "#list" ? "list" : "chat");
window.addEventListener("focus", () => { if (!$("view-list").hidden) loadItems(); });
