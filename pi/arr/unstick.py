"""Finish Internet Archive downloads stuck one piece short.

qBittorrent skips the archive's extra files (setup.py: the .ogv copy, _meta files, thumbnails).
But a piece can straddle the end of the video and the start of a skipped file, and qBittorrent
won't fetch a skipped file's bytes from archive.org's web seeds, so that last piece never comes
and Radarr/Sonarr wait forever (seen 2026-10-18 at 99.3%).

For each radarr/sonarr torrent whose only missing pieces are such shared ones, this wants the
skipped neighbours for a moment, waits for those pieces (up to two minutes), then skips them
again. A few MB of the extra file are fetched; it stays named ".!qB" (incomplete), so neither
app imports it. Dry run by default; --apply to act. Runs on the Pi, from cron with reclaim.py."""
import json, sys, time, urllib.parse, urllib.request

APPLY = "--apply" in sys.argv
QBT = "http://127.0.0.1:8085/api/v2"
WAIT = 120

def qbt(path, **form):
    data = urllib.parse.urlencode(form).encode() if form else None
    raw = urllib.request.urlopen(urllib.request.Request(QBT + path, data=data), timeout=60).read()
    return json.loads(raw) if raw[:1] in (b"[", b"{") else None

def stuck_pieces(t):
    """Missing pieces shared with a skipped file, and those files' indexes; None if other pieces are missing too."""
    files = qbt("/torrents/files", hash=t["hash"])
    states = qbt("/torrents/pieceStates", hash=t["hash"])
    wanted = {p for f in files if f["priority"] > 0 for p in range(f["piece_range"][0], f["piece_range"][1] + 1)}
    missing = {p for p in wanted if states[p] != 2}
    neighbours = {f["index"]: set(range(f["piece_range"][0], f["piece_range"][1] + 1)) & missing
                  for f in files if f["priority"] == 0}
    neighbours = {i: pieces for i, pieces in neighbours.items() if pieces}
    shared = set().union(*neighbours.values()) if neighbours else set()
    return (shared, sorted(neighbours)) if missing and missing == shared else None

for t in qbt("/torrents/info"):
    if t["category"] not in ("radarr", "sonarr") or t["progress"] >= 1 or t["state"] not in ("stalledDL", "downloading"):
        continue
    found = stuck_pieces(t)
    if found is None:
        continue
    pieces, files = found
    if not APPLY:
        print(f"would fetch {len(pieces)} shared piece(s) for '{t['name']}'; --apply to do it")
        continue
    ids = "|".join(map(str, files))
    qbt("/torrents/filePrio", hash=t["hash"], id=ids, priority=1)
    try:
        deadline = time.time() + WAIT
        while time.time() < deadline:
            time.sleep(5)
            states = qbt("/torrents/pieceStates", hash=t["hash"])
            if all(states[p] == 2 for p in pieces):
                break
    finally:
        qbt("/torrents/filePrio", hash=t["hash"], id=ids, priority=0)
    done = all(qbt("/torrents/pieceStates", hash=t["hash"])[p] == 2 for p in pieces)
    print(f"{'finished' if done else 'still stuck after ' + str(WAIT) + ' s:'} '{t['name']}'")
