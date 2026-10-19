# pi-flix: notes for coding agents (Codex, Claude Code)

Home media server on the Raspberry Pi that also hosts risky-business. Everything runs in Docker containers on the Pi; the laptop only administers it. Read [docs/media-server-workflow.svg](docs/media-server-workflow.svg) for the one-page picture, [docs/media-server.md](docs/media-server.md) for how everything fits, and [docs/roadmap.md](docs/roadmap.md) for what's next. Keep them up to date when the setup changes: media-server.md is the story of how it works *now*.

## Machines and access

- **Laptop** (admin only, runs no part of the media server): Windows 10, PowerShell 5.1 (no `&&`, `?:`, `utf8NoBOM`). Git Bash is available.
- **Pi:** `pieasypieces`, Raspberry Pi 5, 8 GB, Debian 13, aarch64, Docker 29 with Compose v5. Hostname resolution from the laptop is flaky, so connect by IP:

  ```bash
  "/c/Program Files/Git/usr/bin/ssh.exe" -o BatchMode=yes -o HostKeyAlias=pieasypieces upendra@192.168.1.40 '<command>'
  ```

  Use Git's `ssh`/`scp`; Windows' built-in OpenSSH is too old. The key is `~/.ssh/id_rsa`, and `upendra` has sudo.
- Windows `curl` fails certificate-revocation checks. Run HTTPS checks from the Pi.
- Never print secrets: the *arr API keys (`/opt/arr/*/config.xml`), `/opt/watchlist/.env`, and the risky-business `.env` files on the Pi. Prowlarr's search results carry its key inside `downloadUrl`: never print that field.

## What runs where

| Thing | Deployed at | Copy in repo |
| --- | --- | --- |
| Jellyfin 12.1 | Pi `/opt/jellyfin` (config in `config/`, home-only: `config/config/network.xml` `EnableRemoteAccess=false`); shows `/srv/data/library` as `/media`, writable so you can delete what you've watched | `pi/jellyfin/compose.yaml` |
| **The library** | Pi `/srv/data/library/{movies,shows}`: the one folder Jellyfin shows; Radarr/Sonarr file into it | — |
| Prowlarr, Radarr, Sonarr, qBittorrent | Pi `/opt/arr` (settings in one folder per app; web pages on `127.0.0.1` only); data in `/srv/data`. Scripts next to it: `setup.py` (wiring, safe to re-run with sudo), `indexers.py` (run from the laptop via `pi/arr/indexers.ps1`), `unstick.py` and `reclaim.py` (meant for upendra's crontab; not installed yet) | `pi/arr/` |
| Watchlist | Pi `/opt/watchlist` (`.env`, `data/`); deployed by `services/watchlist/deploy.sh` | `pi/watchlist/`, `services/watchlist/` |

The deployed files are the truth. When changing one, update the copy here as well, and commit.

- **Retired 2026-10-19:** the laptop pipeline (MediaTidy filing the laptop's qBittorrent downloads, Syncthing copying them to the Pi's `/srv/media`). It's in git history if ever needed. Films get onto the Pi through the *arr stack, or by copying into `/srv/data/library` directly.

## Watchlist

The chat agent, list and release checker: spec in [docs/watchlist.md](docs/watchlist.md), code in `services/watchlist`, deployed by `services/watchlist/deploy.sh` to Pi `/opt/watchlist` (copy of its compose file in `pi/watchlist/`). Secrets are in `/opt/watchlist/.env`: never print them. It hands every film on the list to Radarr and every show to Sonarr (the *arr stack) to download; it never searches indexers itself. It sits on the *arr Docker network (`arr_arr`), so start `/opt/arr` first. Its **Status** tab reads Prowlarr, Radarr and Sonarr (read-only, plus "test indexer") and alerts on anything broken for 30 minutes.

## Conventions

- C#, latest .NET LTS (SDK pinned in `services/watchlist/global.json`).
- Pin exact versions of container images and tools. Choose releases at least two weeks old.
- The watchlist tracks, alerts, and hands films to Radarr and shows to Sonarr.
- The *arr stack (Pi `/opt/arr`, copy in `pi/arr/`) searches and fetches, with **legal indexers only** (the Internet Archive). Never add, configure or tune indexers for pirated content (`indexers.py` only adds definitions on its `LEGAL` list), and its library `/srv/data/library` is the one Jellyfin shows; torrents and library must stay on one filesystem (imports are hard links).
- **No seeding on the Pi:** qBittorrent stops each torrent when complete (ratio 0), and Radarr/Sonarr remove it after importing, so the library file is the only copy. Deleting in Jellyfin frees the space at once; Radarr/Sonarr unmonitor deleted files, and the watchlist stops searching for them.
- The risky-business repo is **public**. Keep media details out of it.
- Windows paths ignore case: never give a runtime, data or output folder a name that differs from a source folder only in case (deleting a dev `data/` once wiped the source folder `Data/`; the dev folder is now `App_Data`). Before `rm -rf` / `Remove-Item -Recurse` on the laptop, list the target first, and commit new code before cleaning up generated files.
