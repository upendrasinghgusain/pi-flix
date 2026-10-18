"""Add, try out and remove Prowlarr indexers. Runs on the Pi; from the laptop, use indexers.ps1
next to this file, which pipes this script over SSH, so nothing needs deploying.

    list                         what's in Prowlarr, whether it's paused, and which apps have it
    add <definition>             add one of Prowlarr's own definitions (from the LEGAL list below)
    add-torznab <name> <url> [api key]   add a feed of your own (Torznab: torrents)
    add-newznab <name> <url> [api key]   add a feed of your own (Newznab: Usenet)
    test <name>                  test it; a passing test also lifts a pause
    search <name> <query>        search just that indexer and show what comes back
    remove <name>                remove it from Prowlarr (and so from Radarr and Sonarr)

Prowlarr copies every indexer to Radarr and Sonarr (full sync), so adding or removing one here is
all it takes. Legal sources only: see the LEGAL list. Reads Prowlarr's API key from its
config.xml and never prints it."""
import json, re, sys, urllib.error, urllib.parse, urllib.request

PROWLARR = "http://127.0.0.1:9696/api/v1"
KEY = re.search(r"<ApiKey>(\w+)</ApiKey>", open("/opt/arr/prowlarr/config.xml").read()).group(1)

# Prowlarr definitions this pipeline may use: sources that only carry material they're allowed to
# share. Most of Prowlarr's ~580 definitions are piracy sites or private trackers; they don't go
# here (see CLAUDE.md, "legal indexers only"). Add a definition once you've checked the source.
LEGAL = {
    "internetarchive": "Internet Archive: public-domain and freely licensed films, TV and audio",
}

def call(method, path, body=None, timeout=60):
    req = urllib.request.Request(PROWLARR + path, method=method,
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"X-Api-Key": KEY, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        sys.exit(f"Prowlarr said HTTP {e.code}: {e.read().decode()[:600]}")

def find(name):
    for i in call("GET", "/indexer"):
        if i["name"].lower() == name.lower() or (i.get("definitionName") or "").lower() == name.lower():
            return i
    sys.exit(f"No indexer called '{name}'. Run: list")

def set_fields(item, **values):
    for f in item["fields"]:
        if f["name"] in values:
            f["value"] = values[f["name"]]
    return item

def save(item, name):
    item.update(name=name, enable=True, appProfileId=1)
    created = call("POST", "/indexer?forceSave=true", item)
    call("POST", "/command", {"name": "ApplicationIndexerSync"})
    print(f"Added '{name}' (id {created['id']}) and asked Prowlarr to copy it to Radarr and Sonarr.")
    print(f"Next: test '{name}', then search '{name}' <a title>.")

def cmd_list():
    paused = {s["indexerId"]: s for s in call("GET", "/indexerstatus")}
    apps = [a["name"] for a in call("GET", "/applications")]
    indexers = call("GET", "/indexer")
    if not indexers:
        print("No indexers.")
    for i in indexers:
        state = "on" if i["enable"] else "off"
        if i["id"] in paused:
            state += f", paused until {paused[i['id']].get('disabledTill', '?')} (UTC)"
        print(f"{i['id']:>3}  {i['name']:<28} {i.get('definitionName') or i['implementation']:<18} {state}")
    print(f"Synced to: {', '.join(apps) or 'no apps'}")
    print("Allowed definitions for 'add': " + ", ".join(f"{k} ({v})" for k, v in LEGAL.items()))

def cmd_add(definition):
    definition = definition.lower()
    if definition not in LEGAL:
        sys.exit(f"'{definition}' isn't on the LEGAL list in indexers.py. Only legal sources go in this "
                 "pipeline; if this one is, add it to the list with a line saying what it carries.")
    if any(i.get("definitionName") == definition for i in call("GET", "/indexer")):
        sys.exit(f"'{definition}' is already in Prowlarr. Run: list")
    schema = next((s for s in call("GET", "/indexer/schema") if s.get("definitionName") == definition), None)
    if schema is None:
        sys.exit(f"Prowlarr has no definition called '{definition}'.")
    save(schema, schema["name"])

def cmd_add_feed(implementation, name, url, api_key=""):
    schema = next(s for s in call("GET", "/indexer/schema") if s["implementation"] == implementation)
    save(set_fields(schema, baseUrl=url, apiKey=api_key), name)

def cmd_test(name):
    item = find(name)
    req = urllib.request.Request(PROWLARR + "/indexer/test", method="POST", data=json.dumps(item).encode(),
                                 headers={"X-Api-Key": KEY, "Content-Type": "application/json"})
    try:
        urllib.request.urlopen(req, timeout=180)
        print(f"'{item['name']}' works. If it was paused, it isn't now.")
    except urllib.error.HTTPError as e:
        try:
            reasons = "; ".join(x.get("errorMessage", "") for x in json.loads(e.read()))
        except ValueError:
            reasons = f"HTTP {e.code}"
        sys.exit(f"'{item['name']}' failed: {reasons}")
    except (TimeoutError, urllib.error.URLError) as e:
        sys.exit(f"'{item['name']}' didn't answer in time ({e}).")

def cmd_search(name, query):
    item = find(name)
    q = urllib.parse.urlencode({"query": query, "indexerIds": item["id"], "type": "search", "limit": 50})
    results = call("GET", f"/search?{q}", timeout=180)
    print(f"{len(results)} results from '{item['name']}' for '{query}':")
    for r in results[:25]:
        print(f"  {r['size'] / 1e6:8.0f} MB  seeders {r.get('seeders', '-'):>4}  {r['title'][:90]}")

def cmd_remove(name):
    item = find(name)
    call("DELETE", f"/indexer/{item['id']}")
    call("POST", "/command", {"name": "ApplicationIndexerSync"})
    print(f"Removed '{item['name']}' from Prowlarr; Radarr and Sonarr drop it at the next sync.")

COMMANDS = {"list": (cmd_list, 0, 0), "add": (cmd_add, 1, 1), "test": (cmd_test, 1, 1),
            "search": (cmd_search, 2, 2), "remove": (cmd_remove, 1, 1),
            "add-torznab": (lambda *a: cmd_add_feed("Torznab", *a), 2, 3),
            "add-newznab": (lambda *a: cmd_add_feed("Newznab", *a), 2, 3)}

args = sys.argv[1:]
if not args or args[0] not in COMMANDS:
    sys.exit(__doc__)
fn, least, most = COMMANDS[args[0]]
rest = args[1:]
if args[0] == "search" and len(rest) > 2:
    rest = [rest[0], " ".join(rest[1:])]
if not least <= len(rest) <= most:
    sys.exit(__doc__)
fn(*rest)
