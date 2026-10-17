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
- Never print secrets: Syncthing API keys (in each `config.xml`), and the risky-business `.env` files on the Pi.

## What runs where

| Thing | Deployed at | Copy in repo |
| --- | --- | --- |
| Jellyfin 12.1 | Pi `/opt/jellyfin` (config in `config/`, home-only: `config/config/network.xml` `EnableRemoteAccess=false`) | `pi/jellyfin/compose.yaml` |
| Syncthing 2.1.5 (receive-only) | Pi `/opt/syncthing` (settings in `data/`) | `pi/syncthing/compose.yaml` |
| Syncthing 2.1.5 (send-only) | Laptop, winget package; *Syncthing* scheduled task at logon; settings in `%LOCALAPPDATA%\Syncthing` | — |
| Ignore list | `C:\Jellyfin Media\.stignore` (Syncthing writes it) | `laptop/syncthing/.stignore` |
| Prowlarr, Radarr, Sonarr, qBittorrent | Pi `/opt/arr` (settings in one folder per app; web pages on `127.0.0.1` only); data in `/srv/data` | `pi/arr/compose.yaml`, `pi/arr/setup.py` |
| MediaTidy | `%LOCALAPPDATA%\MediaTidy\bin`, run by qBittorrent "on torrent finished" with `--apply "%F"` | `tools/media-tidy` |

The deployed files are the truth. When changing one, update the copy here as well, and commit.

- **Syncthing:** folder id `jellyfin-media`. Both ends are configured through the REST API (`X-API-Key` from `config.xml`), home network only (no global discovery, relays or NAT). On the Pi, the API is reachable only from the Pi itself.
- **Bulk seeding** (USB stick or rsync into `/srv/media`): pause the laptop's folder, copy, **rescan the Pi's folder**, then un-pause. Otherwise the Pi downloads everything again. If a file shows as *locally changed* on the Pi, revert it (`POST /rest/db/revert`); that rebuilds it from local data.

## MediaTidy

```powershell
cd tools/media-tidy
dotnet test
dotnet publish src/MediaTidy -c Release -o "$env:LOCALAPPDATA\MediaTidy\bin"
& "$env:LOCALAPPDATA\MediaTidy\bin\MediaTidy.exe" | Out-Host    # dry run over C:\qBittorrent
```

It's a WinExe so it never shows a window. Pipe it to see output. It must never delete or move the originals (qBittorrent seeds them); it only hard-links. Add a test for every new naming pattern.

## Watchlist

The chat agent, list and release checker: spec in [docs/watchlist.md](docs/watchlist.md), code in `services/watchlist`, deployed by `services/watchlist/deploy.sh` to Pi `/opt/watchlist` (copy of its compose file in `pi/watchlist/`). Secrets are in `/opt/watchlist/.env`: never print them. It tracks and alerts only; it never searches for or fetches downloads.

## Conventions

- C#, latest .NET LTS (SDK pinned in `tools/media-tidy/global.json`).
- Pin exact versions of container images and tools. Choose releases at least two weeks old.
- MediaTidy's scope is filing finished downloads. The watchlist tracks and alerts only.
- The *arr stack (Pi `/opt/arr`, copy in `pi/arr/`) searches and fetches, with **legal indexers only** (the Internet Archive). Never add, configure or tune indexers for pirated content, and keep its folders (`/srv/data`) apart from `/srv/media`.
- The risky-business repo is **public**. Keep media details out of it.
