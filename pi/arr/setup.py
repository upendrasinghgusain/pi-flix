"""Wire Prowlarr, Radarr, Sonarr and qBittorrent together through their REST APIs.
Runs on the Pi. Reads API keys from each config.xml and never prints them. Safe to re-run."""
import json, re, urllib.request, urllib.error

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
