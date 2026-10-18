# pi-flix: notes for Claude

Home media server on the Raspberry Pi that also hosts risky-business. Read [docs/media-server.md](docs/media-server.md) for how everything fits, and [docs/roadmap.md](docs/roadmap.md) for what's next. Keep both up to date when the setup changes: media-server.md is the story of how it works *now*.

## Machines and access

- **Laptop:** Windows 10, PowerShell 5.1 (no `&&`, `?:`, `utf8NoBOM`). Git Bash is available.
- **Pi:** `pieasypieces`, Raspberry Pi 5, 8 GB, Debian 13, aarch64, Docker 29 with Compose v5. Hostname resolution from the laptop is flaky, so connect by IP:

  ```bash
  "/c/Program Files/Git/usr/bin/ssh.exe" -o BatchMode=yes -o HostKeyAlias=pieasypieces upendra@192.168.1.40 '<command>'
  ```

  Use Git's `ssh`/`scp`; Windows' built-in OpenSSH is too old. The key is `~/.ssh/id_rsa`, and `upendra` has sudo.
- Windows `curl` fails certificate-revocation checks. Run HTTPS checks from the Pi.
- Never print secrets: the *arr API keys (`/opt/arr/*/config.xml`), Syncthing API keys (in each `config.xml`), `/opt/watchlist/.env`, and the risky-business `.env` files on the Pi. Prowlarr's search results carry its key inside `downloadUrl`: never print that field.

## What runs where

| Thing | Deployed at | Copy in repo |
| --- | --- | --- |
| Jellyfin 12.1 | Pi `/opt/jellyfin` (config in `config/`, home-only: `config/config/network.xml` `EnableRemoteAccess=false`); shows `/srv/data/library` as `/media`, writable so you can delete what you've watched | `pi/jellyfin/compose.yaml` |
| **The library** | Pi `/srv/data/library/{movies,shows}`: the one folder Jellyfin shows; Radarr/Sonarr file into it | — |
| Syncthing 2.1.5 (receive-only), **stopped** since 2026-10-18 | Pi `/opt/syncthing` (settings in `data/`) | `pi/syncthing/compose.yaml` |
| Syncthing 2.1.5 (send-only), **folder paused** | Laptop, winget package; *Syncthing* scheduled task at logon; settings in `%LOCALAPPDATA%\Syncthing` | — |
| Ignore list | `C:\Jellyfin Media\.stignore` (Syncthing writes it) | `laptop/syncthing/.stignore` |
| Prowlarr, Radarr, Sonarr, qBittorrent | Pi `/opt/arr` (settings in one folder per app; web pages on `127.0.0.1` only); data in `/srv/data`. Scripts next to it: `setup.py` (wiring, safe to re-run with sudo), `indexers.py` (run from the laptop via `pi/arr/indexers.ps1`), `unstick.py` and `reclaim.py` (meant for upendra's crontab; not installed yet) | `pi/arr/` |
| Watchlist | Pi `/opt/watchlist` (`.env`, `data/`); deployed by `services/watchlist/deploy.sh` | `pi/watchlist/`, `services/watchlist/` |
| MediaTidy | `%LOCALAPPDATA%\MediaTidy\bin`, run by qBittorrent "on torrent finished" with `--apply "%F"` | `tools/media-tidy` |

The deployed files are the truth. When changing one, update the copy here as well, and commit.

- **Syncthing is paused** (Pi container down, laptop folder paused): laptop films no longer reach the Pi. `/srv/media` is empty; don't point anything at it. Don't restart Syncthing onto `/srv/data/library` as receive-only: Radarr's films would show as local changes, and *Revert* would delete them.
- **Syncthing (when it ran):** folder id `jellyfin-media`. Both ends are configured through the REST API (`X-API-Key` from `config.xml`), home network only (no global discovery, relays or NAT). On the Pi, the API is reachable only from the Pi itself.
- **Bulk loading while Syncthing ran** (USB stick or rsync into `/srv/media`): pause the laptop's folder, copy, **rescan the Pi's folder**, then un-pause. Otherwise the Pi downloads everything again. If a file shows as *locally changed* on the Pi, revert it (`POST /rest/db/revert`); that rebuilds it from local data.

## MediaTidy

```powershell
cd tools/media-tidy
dotnet test
dotnet publish src/MediaTidy -c Release -o "$env:LOCALAPPDATA\MediaTidy\bin"
& "$env:LOCALAPPDATA\MediaTidy\bin\MediaTidy.exe" | Out-Host    # dry run over C:\qBittorrent
```

It's a WinExe so it never shows a window. Pipe it to see output. It must never delete or move the originals (the laptop's qBittorrent seeds them); it only hard-links. Add a test for every new naming pattern.

## Watchlist

The chat agent, list and release checker: spec in [docs/watchlist.md](docs/watchlist.md), code in `services/watchlist`, deployed by `services/watchlist/deploy.sh` to Pi `/opt/watchlist` (copy of its compose file in `pi/watchlist/`). Secrets are in `/opt/watchlist/.env`: never print them. It hands every film on the list to Radarr (the *arr stack) to download; it never searches indexers itself. It sits on the *arr Docker network (`arr_arr`), so start `/opt/arr` first. Its **Status** tab reads Prowlarr, Radarr and Sonarr (read-only, plus "test indexer") and alerts on anything broken for 30 minutes.

## Conventions

- C#, latest .NET LTS (SDK pinned in `tools/media-tidy/global.json`).
- Pin exact versions of container images and tools. Choose releases at least two weeks old.
- MediaTidy's scope is filing finished downloads. The watchlist tracks, alerts, and hands films to Radarr.
- The *arr stack (Pi `/opt/arr`, copy in `pi/arr/`) searches and fetches, with **legal indexers only** (the Internet Archive). Never add, configure or tune indexers for pirated content (`indexers.py` only adds definitions on its `LEGAL` list), and its library `/srv/data/library` is the one Jellyfin shows; torrents and library must stay on one filesystem (imports are hard links).
- **No seeding on the Pi:** qBittorrent stops each torrent when complete (ratio 0), and Radarr/Sonarr remove it after importing, so the library file is the only copy. Deleting in Jellyfin frees the space at once; Radarr/Sonarr unmonitor deleted files, and the watchlist stops searching for them.
- The risky-business repo is **public**. Keep media details out of it.
