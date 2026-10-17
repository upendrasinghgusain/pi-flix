"""Wire Prowlarr, Radarr, Sonarr and qBittorrent together through their REST APIs, and link
Radarr with the watchlist (/opt/watchlist). Runs on the Pi. Reads API keys from each config.xml
and never prints them or the webhook secret. Safe to re-run."""
import json, os, re, secrets, urllib.request, urllib.error

def key(app):
    return re.search(r"<ApiKey>(\w+)</ApiKey>", open(f"/opt/arr/{app}/config.xml").read()).group(1)

APPS = {
    "radarr":   ("http://127.0.0.1:7878/api/v3", key("radarr")),
    "sonarr":   ("http://127.0.0.1:8989/api/v3", key("sonarr")),
    "prowlarr": ("http://127.0.0.1:9696/api/v1", key("prowlarr")),
}

def call(app, method, path, body=None):
    base, k = APPS[app]
    req = urllib.request.Request(base + path, method=method,
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"X-Api-Key": k, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{app} {method} {path}: HTTP {e.code} {e.read().decode()[:600]}")

def set_fields(item, **values):
    for f in item["fields"]:
        if f["name"] in values:
            f["value"] = values[f["name"]]
    return item

def from_schema(app, kind, implementation, name, **fields):
    existing = [x for x in call(app, "GET", f"/{kind}") if x["name"] == name]
    if existing:
        print(f"{app}: {kind} '{name}' already there")
        return existing[0]
    item = next(s for s in call(app, "GET", f"/{kind}/schema") if s["implementation"] == implementation)
    item = set_fields(item, **fields)
    item["name"] = name
    item["enable"] = True
    created = call(app, "POST", f"/{kind}?forceSave=true", item)
    print(f"{app}: added {kind} '{name}'")
    return created

def root_folder(app, path):
    if any(r["path"].rstrip("/") == path for r in call(app, "GET", "/rootfolder")):
        print(f"{app}: root folder {path} already there")
    else:
        call(app, "POST", "/rootfolder", {"path": path})
        print(f"{app}: added root folder {path}")

def rename_on(app, flag):
    naming = call(app, "GET", "/config/naming")
    naming[flag] = True
    call(app, "PUT", f"/config/naming/{naming['id']}", naming)
    print(f"{app}: renaming on")

# Radarr and Sonarr: where the library lives, renaming, and the download client.
root_folder("radarr", "/data/library/movies")
root_folder("sonarr", "/data/library/tv")
rename_on("radarr", "renameMovies")
rename_on("sonarr", "renameEpisodes")
from_schema("radarr", "downloadclient", "QBittorrent", "qBittorrent",
            host="qbittorrent", port=8085, movieCategory="radarr")
from_schema("sonarr", "downloadclient", "QBittorrent", "qBittorrent",
            host="qbittorrent", port=8085, tvCategory="sonarr")

# Prowlarr: the one legal indexer, then hand it to Radarr and Sonarr.
indexers = call("prowlarr", "GET", "/indexer")
if any(i.get("definitionName") == "internetarchive" for i in indexers):
    print("prowlarr: Internet Archive already there")
else:
    ia = next(s for s in call("prowlarr", "GET", "/indexer/schema") if s.get("definitionName") == "internetarchive")
    ia["enable"] = True
    ia["appProfileId"] = 1
    call("prowlarr", "POST", "/indexer?forceSave=true", ia)
    print("prowlarr: added Internet Archive")

for app, port in [("radarr", 7878), ("sonarr", 8989)]:
    from_schema("prowlarr", "applications", app.capitalize(), app.capitalize(),
                prowlarrUrl="http://prowlarr:9696", baseUrl=f"http://{app}:{port}", apiKey=APPS[app][1])
    for a in call("prowlarr", "GET", "/applications"):
        if a["name"] == app.capitalize() and a.get("syncLevel") != "fullSync":
            a["syncLevel"] = "fullSync"
            call("prowlarr", "PUT", f"/applications/{a['id']}", a)

call("prowlarr", "POST", "/command", {"name": "ApplicationIndexerSync"})
print("prowlarr: sync requested")

# Radarr's "Watchlist" quality profile. Internet Archive release names rarely say their quality,
# so Radarr calls them Unknown; the built-in HD profiles would reject them all. This accepts
# Unknown and SD to 1080p, never upgrades (no re-downloads), and Unknown is capped below.
WANTED = {"Unknown", "SDTV", "DVD", "WEBDL-480p", "WEBRip-480p", "Bluray-480p", "Bluray-576p",
          "HDTV-720p", "WEBDL-720p", "WEBRip-720p", "Bluray-720p",
          "HDTV-1080p", "WEBDL-1080p", "WEBRip-1080p", "Bluray-1080p"}
if any(p["name"] == "Watchlist" for p in call("radarr", "GET", "/qualityprofile")):
    print("radarr: Watchlist profile already there")
else:
    profile = call("radarr", "GET", "/qualityprofile/schema")
    for item in profile["items"]:
        children = item["items"] if not item.get("quality") else [item]
        for child in children:
            child["allowed"] = child["quality"]["name"] in WANTED
        item["allowed"] = any(c["allowed"] for c in children)
        if any(c["quality"]["name"] == "Bluray-1080p" for c in children):
            profile["cutoff"] = item["quality"]["id"] if item.get("quality") else item["id"]
    profile.update(name="Watchlist", upgradeAllowed=False)
    call("radarr", "POST", "/qualityprofile", profile)
    print("radarr: added Watchlist profile")

# Size limits are in MB per minute of running time. Unknown's default (100) would allow a
# 9 GB copy of a 90-minute film; 30 caps it near 2.7 GB. Tune these in Radarr: Settings > Quality.
for d in call("radarr", "GET", "/qualitydefinition"):
    if d["quality"]["name"] == "Unknown" and d["maxSize"] != 30:
        d.update(maxSize=30, preferredSize=20)
        call("radarr", "PUT", f"/qualitydefinition/{d['id']}", d)
        print("radarr: Unknown capped at 30 MB/min")

# The watchlist: its .env gets Radarr's address, API key and a webhook secret; Radarr gets a
# webhook that calls the watchlist on grab, import and delete.
ENV = "/opt/watchlist/.env"
lines = open(ENV).read().splitlines()
values = dict(l.split("=", 1) for l in lines if "=" in l and not l.startswith("#"))
wanted = {"Radarr__Url": "http://radarr:7878", "Radarr__ApiKey": APPS["radarr"][1],
          "Radarr__WebhookSecret": values.get("Radarr__WebhookSecret") or secrets.token_urlsafe(32)}
for name, value in wanted.items():
    if values.get(name) != value:
        lines = [l for l in lines if not l.startswith(name + "=")] + [f"{name}={value}"]
        print(f"watchlist: set {name} in .env")
with open(ENV, "w") as f:
    f.write("\n".join(lines) + "\n")
os.chmod(ENV, 0o600)

hook = next((n for n in call("radarr", "GET", "/notification") if n["name"] == "Watchlist"), None)
if hook is None:
    hook = next(s for s in call("radarr", "GET", "/notification/schema") if s["implementation"] == "Webhook")
hook = set_fields(hook, url="http://watchlist:8190/hooks/radarr", method=1,
                  username="radarr", password=wanted["Radarr__WebhookSecret"])
hook.update(name="Watchlist", onGrab=True, onDownload=True, onUpgrade=True,
            onMovieDelete=True, onMovieFileDelete=True, onMovieFileDeleteForUpgrade=False)
if "id" in hook:
    call("radarr", "PUT", f"/notification/{hook['id']}?forceSave=true", hook)
    print("radarr: updated Watchlist webhook")
else:
    call("radarr", "POST", "/notification?forceSave=true", hook)
    print("radarr: added Watchlist webhook")
