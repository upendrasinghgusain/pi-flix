# pi-flix

A home media server on a Raspberry Pi: films and TV from the laptop to the Sony TV, with no subscription required.

```text
qBittorrent ──► MediaTidy ──► C:\Jellyfin Media ──► Syncthing ──► Pi /srv/media ──► Jellyfin ──► Sony TV
 (laptop)       tidy names      master copy          home Wi-Fi                     posters,     Jellyfin
                hard links                           encrypted                      details      app
```

Home network only. Watching needs the home Wi-Fi **and** a Jellyfin account.

## Start here

- **[docs/media-server.md](docs/media-server.md)**: the whole setup as a story: every step, prerequisite and concept, and who can access what.
- **[docs/roadmap.md](docs/roadmap.md)**: what's next (Ethernet, bigger storage, remote access, photos and documents).

## Repository layout

```text
docs/                      how it works, and what's next
pi/jellyfin/               compose.yaml running on the Pi at /opt/jellyfin
pi/syncthing/              compose.yaml running on the Pi at /opt/syncthing
laptop/syncthing/          the Syncthing ignore list used for C:\Jellyfin Media
tools/media-tidy/          MediaTidy: files finished downloads into the Jellyfin layout (C#, .NET 10)
```

The files in `pi/` and `laptop/` are copies of what is deployed. When you change one, change both. See [CLAUDE.md](CLAUDE.md).

## Addresses

| What | Where |
| --- | --- |
| Jellyfin | <http://192.168.1.40:8096> (home network only) |
| Syncthing on the laptop | <http://127.0.0.1:8384> |
| Syncthing on the Pi | `ssh -L 8385:127.0.0.1:8384 upendra@192.168.1.40`, then <http://127.0.0.1:8385> |
| MediaTidy log | `%LOCALAPPDATA%\MediaTidy\mediatidy.log` |

## Related

The same Pi also hosts [risky-business](https://github.com/upendrasinghgusain/risky-business), which is deployed separately and is a public repo. Nothing from here belongs there.
