"""Free the space of films and episodes you've deleted from the library (in Jellyfin, say).

Radarr and Sonarr import by hard link: the library file and the seeding torrent's file are two
names for the same data, and the space only comes back when both are gone. This finds finished
torrents (categories radarr and sonarr) that the app imported but that no longer have any file
left in the library, and removes them with their data from qBittorrent.

A torrent is kept if the app never imported it, if it's still in the app's queue, or if any of
its files still has a second name. Dry run by default; --apply to remove. Runs on the Pi, from
upendra's crontab (see docs/media-server.md). Reads API keys from each config.xml; never prints them."""
import json, os, re, sys, urllib.parse, urllib.request

APPLY = "--apply" in sys.argv
QBT = "http://127.0.0.1:8085/api/v2"
APPS = {"radarr": "http://127.0.0.1:7878/api/v3", "sonarr": "http://127.0.0.1:8989/api/v3"}

def key(app):
    return re.search(r"<ApiKey>(\w+)</ApiKey>", open(f"/opt/arr/{app}/config.xml").read()).group(1)

def arr(app, path):
    req = urllib.request.Request(APPS[app] + path, headers={"X-Api-Key": key(app)})
    return json.load(urllib.request.urlopen(req, timeout=60))

def qbt(path, **form):
    data = urllib.parse.urlencode(form).encode() if form else None
    raw = urllib.request.urlopen(urllib.request.Request(QBT + path, data=data), timeout=60).read()
    return json.loads(raw) if raw.startswith((b"[", b"{")) else None

def host_path(path):
    # qBittorrent sees /srv/data/torrents as /data/torrents.
    return "/srv" + path if path.startswith("/data/") else path

def imported(app, download_id):
    # eventType 3 is "downloadFolderImported" in both Radarr and Sonarr.
    q = urllib.parse.urlencode({"downloadId": download_id.upper(), "eventType": 3, "pageSize": 1})
    return arr(app, f"/history?{q}")["totalRecords"] > 0

queued = {app: {r.get("downloadId", "").upper() for r in arr(app, "/queue?pageSize=500")["records"]}
          for app in APPS}

for t in qbt("/torrents/info"):
    app = t["category"]
    if app not in APPS or t["progress"] < 1:
        continue
    h = t["hash"].upper()
    if h in queued[app] or not imported(app, h):
        continue
    files = [f for f in qbt("/torrents/files", hash=t["hash"]) if f["priority"] > 0]
    root = host_path(t["save_path"])
    paths = [os.path.join(root, f["name"]) for f in files]
    if any(os.path.exists(p) and os.stat(p).st_nlink > 1 for p in paths):
        continue
    size = sum(os.path.getsize(p) for p in paths if os.path.exists(p)) / 1e9
    if APPLY:
        qbt("/torrents/delete", hashes=t["hash"], deleteFiles="true")
        print(f"removed {app} torrent '{t['name']}' and its data ({size:.1f} GB)")
    else:
        print(f"would remove {app} torrent '{t['name']}' and its data ({size:.1f} GB); --apply to do it")
