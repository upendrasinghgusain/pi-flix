# pi-flix

A home media server on a Raspberry Pi: films and TV to the Sony TV, with no subscription required.

```text
watchlist chat ──► Radarr / Sonarr ──► Prowlarr ──► Internet Archive (legal sources only)
 (phone)                │
                        ▼
                  qBittorrent on the Pi ──► /srv/data/library ──► Jellyfin ──► Sony TV
                  (no seeding: removed       the one library      posters,     Jellyfin
                   once imported)                                 details      app
```

Everything runs in Docker containers on the Pi. Home network only. Watching needs the home Wi-Fi **and** a Jellyfin account. Delete something in Jellyfin and the space comes back at once; it isn't downloaded again.

## Start here

- **[docs/media-server-workflow.svg](docs/media-server-workflow.svg)**: the media workflow diagram.
- **[docs/media-server.md](docs/media-server.md)**: how it works now, every step and concept, who can access what, and day-to-day tasks.
- **[docs/watchlist.md](docs/watchlist.md)**: the chat app's design: adding titles, release alerts, downloading through Radarr, the Status tab.
- **[docs/roadmap.md](docs/roadmap.md)**: what's next (Ethernet, bigger storage, backups, remote access, photos and documents).

## Repository layout

```text
docs/                      how it works, and what's next
pi/jellyfin/               compose.yaml running on the Pi at /opt/jellyfin
pi/arr/                    the *arr stack at /opt/arr: compose.yaml and its scripts
                             setup.py     wires the apps together (safe to re-run)
                             indexers.py  list/add/test/search/remove indexers (indexers.ps1 runs it from the laptop)
                             unstick.py   finishes Internet Archive downloads stuck one piece short
                             reclaim.py   safety net: removes torrents whose library file is gone
pi/watchlist/              the watchlist's compose file, Dockerfile, .env and settings examples
services/watchlist/        the watchlist: chat agent, list, release checker, Status tab (C#, .NET 10)
```

The files in `pi/` are copies of what is deployed. When you change one, change both. See [CLAUDE.md](CLAUDE.md).

## Addresses

| What | Where |
| --- | --- |
| Jellyfin | <http://192.168.1.40:8096> (home network only) |
| Watchlist (chat, list, Status) | <https://chat.upendragusain.co.uk> (Cloudflare Access login) |
| Prowlarr, Radarr, Sonarr, qBittorrent | SSH tunnel, then `http://localhost:9696`, `7878`, `8989`, `8085` ([command](docs/media-server.md#the-arr-pipeline-public-domain-titles)) |

## Related

The same Pi also hosts [risky-business](https://github.com/upendrasinghgusain/risky-business), which is deployed separately and is a public repo. Nothing from here belongs there.
