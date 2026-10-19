"""Wire Prowlarr, Radarr, Sonarr and qBittorrent together through their REST APIs, and link
Radarr with the watchlist (/opt/watchlist). Runs on the Pi. Reads API keys from each config.xml
and never prints them or the webhook secret. Safe to re-run."""
import json, os, re, secrets, urllib.parse, urllib.request, urllib.error

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

# qBittorrent: skip the Internet Archive's extras. Its _meta files change after the torrent is
# made, so they never finish, and Radarr waits for the whole torrent before importing.
# The .ogv is a second, lower-quality copy of the video.
QBT = "http://127.0.0.1:8085/api/v2"
# Unfinished files are named ".!qB", so Radarr and Sonarr never import one: a skipped file can be
# partly on disk (see unstick.py), and Sonarr once imported a half-fetched .ogv over the .mp4.
SKIP = ["*_meta.xml", "*_meta.sqlite", "*_files.xml", "__ia_thumb.jpg", "*.ogv", "*.gif", "*.png", "*.asr.js", "*.asr.srt"]
# No seeding: a torrent stops as soon as it's complete (ratio 0, action 0 = stop). Radarr and
# Sonarr import it, then remove it with its data ("remove completed downloads", on by default),
# so the library file is the only copy and deleting it in Jellyfin frees the space at once.
prefs = {"excluded_file_names_enabled": True, "excluded_file_names": "\n".join(SKIP), "incomplete_files_ext": True,
         "max_ratio_enabled": True, "max_ratio": 0, "max_ratio_act": 0}
urllib.request.urlopen(urllib.request.Request(f"{QBT}/app/setPreferences",
                       data=urllib.parse.urlencode({"json": json.dumps(prefs)}).encode()), timeout=30)
print("qbittorrent: Internet Archive extras skipped, no seeding, unfinished files named .!qB")

# Radarr and Sonarr: where the library lives, renaming, and the download client.
root_folder("radarr", "/data/library/movies")
root_folder("sonarr", "/data/library/shows")
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

# No RSS checks: for the Internet Archive that's "every film, newest first", which takes ~30 s
# uncached and times out. Each timeout pauses the indexer, blocking real searches for a while.
# Searches for a named film take under a second and stay on.
for profile in call("prowlarr", "GET", "/appprofile"):
    if profile["enableRss"]:
        profile["enableRss"] = False
        call("prowlarr", "PUT", f"/appprofile/{profile['id']}", profile)
        print(f"prowlarr: RSS off in sync profile '{profile['name']}'")

for app, port in [("radarr", 7878), ("sonarr", 8989)]:
    from_schema("prowlarr", "applications", app.capitalize(), app.capitalize(),
                prowlarrUrl="http://prowlarr:9696", baseUrl=f"http://{app}:{port}", apiKey=APPS[app][1])
    for a in call("prowlarr", "GET", "/applications"):
        if a["name"] == app.capitalize() and a.get("syncLevel") != "fullSync":
            a["syncLevel"] = "fullSync"
            call("prowlarr", "PUT", f"/applications/{a['id']}", a)

call("prowlarr", "POST", "/command", {"name": "ApplicationIndexerSync"})
print("prowlarr: sync requested")

# Deleting a file you've watched (in Jellyfin, say) unmonitors it, so it isn't downloaded again.
for app, flag in [("radarr", "autoUnmonitorPreviouslyDownloadedMovies"),
                  ("sonarr", "autoUnmonitorPreviouslyDownloadedEpisodes")]:
    mm = call(app, "GET", "/config/mediamanagement")
    if not mm[flag]:
        mm[flag] = True
        call(app, "PUT", f"/config/mediamanagement/{mm['id']}", mm)
        print(f"{app}: deleted files are unmonitored")

# The "Watchlist" quality profile, in Radarr and Sonarr. Internet Archive release names rarely say
# their quality, so the apps call them Unknown; the built-in HD profiles would reject them all.
# This accepts Unknown and SD to 1080p, never upgrades (no re-downloads), and Unknown is capped below.
WANTED = {"Unknown", "SDTV", "DVD", "WEBDL-480p", "WEBRip-480p", "Bluray-480p", "Bluray-576p",
          "HDTV-720p", "WEBDL-720p", "WEBRip-720p", "Bluray-720p",
          "HDTV-1080p", "WEBDL-1080p", "WEBRip-1080p", "Bluray-1080p"}

def watchlist_profile(app):
    if any(p["name"] == "Watchlist" for p in call(app, "GET", "/qualityprofile")):
        print(f"{app}: Watchlist profile already there")
        return
    profile = call(app, "GET", "/qualityprofile/schema")
    for item in profile["items"]:
        children = item["items"] if not item.get("quality") else [item]
        for child in children:
            child["allowed"] = child["quality"]["name"] in WANTED
        item["allowed"] = any(c["allowed"] for c in children)
        if any(c["quality"]["name"] == "Bluray-1080p" for c in children):
            profile["cutoff"] = item["quality"]["id"] if item.get("quality") else item["id"]
    profile.update(name="Watchlist", upgradeAllowed=False)
    call(app, "POST", "/qualityprofile", profile)
    print(f"{app}: added Watchlist profile")

# Custom formats score each release; the Watchlist profile rejects anything scoring below 0.
# The apps pick by quality first, then by score, then by size closest to the preferred size.
#   Over 5 GB          -10000  a hard cap per film or episode, whatever the running time
#   Trailer or sample  -10000  the Internet Archive has plenty
#   Looks HD             +100  names saying 720p, 1080p, HD, Blu-ray, WEB-DL: preferred among
#                              equals (most Internet Archive names give no quality at all)
# Change them in each app: Settings > Custom Formats, and the scores under Settings > Profiles.
def custom_format(app, name, implementation, **fields):
    existing = next((f for f in call(app, "GET", "/customformat") if f["name"] == name), None)
    spec = next(s for s in call(app, "GET", "/customformat/schema") if s["implementation"] == implementation)
    spec = set_fields(spec, **fields)
    spec["name"] = name
    body = {"name": name, "includeCustomFormatWhenRenaming": False, "specifications": [spec]}
    if existing:
        body["id"] = existing["id"]
        call(app, "PUT", f"/customformat/{existing['id']}", body)
    else:
        call(app, "POST", "/customformat", body)
        print(f"{app}: added custom format '{name}'")

SCORES = {"Over 5 GB": -10000, "Trailer or sample": -10000, "Looks HD": 100}
for app in ("radarr", "sonarr"):
    watchlist_profile(app)
    custom_format(app, "Over 5 GB", "SizeSpecification", min=5, max=100000)
    custom_format(app, "Trailer or sample", "ReleaseTitleSpecification", value=r"\b(trailer|teaser|sample|featurette)\b")
    custom_format(app, "Looks HD", "ReleaseTitleSpecification", value=r"\b(720p|1080p|HD|Blu-?Ray|BDRip|WEB-?DL|WEB-?Rip)\b")
    profile = next(p for p in call(app, "GET", "/qualityprofile") if p["name"] == "Watchlist")
    for item in profile["formatItems"]:
        item["score"] = SCORES.get(item["name"], item["score"])
    profile["minFormatScore"] = 0
    call(app, "PUT", f"/qualityprofile/{profile['id']}", profile)
    print(f"{app}: Watchlist profile scores set")

# Size per minute of running time: back to Radarr's defaults for Unknown (an earlier version of
# this script capped it at 30), since "Over 5 GB" is the cap now.
for d in call("radarr", "GET", "/qualitydefinition"):
    if d["quality"]["name"] == "Unknown" and d["maxSize"] == 30:
        d.update(maxSize=100, preferredSize=95)
        call("radarr", "PUT", f"/qualitydefinition/{d['id']}", d)
        print("radarr: Unknown size limits back to defaults")

# The watchlist: its .env gets Radarr's and Sonarr's addresses, API keys and a webhook secret;
# Radarr and Sonarr each get a webhook that calls the watchlist on grab, import and delete (both
# use user "radarr" and the same secret). Prowlarr's address and key are for its Status tab, which
# only reads them (and can ask Prowlarr to test an indexer).
ENV = "/opt/watchlist/.env"
lines = open(ENV).read().splitlines()
values = dict(l.split("=", 1) for l in lines if "=" in l and not l.startswith("#"))
wanted = {"Radarr__Url": "http://radarr:7878", "Radarr__ApiKey": APPS["radarr"][1],
          "Radarr__WebhookSecret": values.get("Radarr__WebhookSecret") or secrets.token_urlsafe(32),
          "Prowlarr__Url": "http://prowlarr:9696", "Prowlarr__ApiKey": APPS["prowlarr"][1],
          "Sonarr__Url": "http://sonarr:8989", "Sonarr__ApiKey": APPS["sonarr"][1]}
for name, value in wanted.items():
    if values.get(name) != value:
        lines = [l for l in lines if not l.startswith(name + "=")] + [f"{name}={value}"]
        print(f"watchlist: set {name} in .env")
with open(ENV, "w") as f:
    f.write("\n".join(lines) + "\n")
os.chmod(ENV, 0o600)

def watchlist_hook(app, **events):
    hook = next((n for n in call(app, "GET", "/notification") if n["name"] == "Watchlist"), None)
    if hook is None:
        hook = next(s for s in call(app, "GET", "/notification/schema") if s["implementation"] == "Webhook")
    hook = set_fields(hook, url=f"http://watchlist:8190/hooks/{app}", method=1,
                      username="radarr", password=wanted["Radarr__WebhookSecret"])
    hook.update(name="Watchlist", onGrab=True, onDownload=True, onUpgrade=True, **events)
    if "id" in hook:
        call(app, "PUT", f"/notification/{hook['id']}?forceSave=true", hook)
        print(f"{app}: updated Watchlist webhook")
    else:
        call(app, "POST", "/notification?forceSave=true", hook)
        print(f"{app}: added Watchlist webhook")

watchlist_hook("radarr", onMovieDelete=True, onMovieFileDelete=True, onMovieFileDeleteForUpgrade=False)
watchlist_hook("sonarr", onSeriesDelete=True, onEpisodeFileDelete=True, onEpisodeFileDeleteForUpgrade=False)
