# MediaTidy

Files finished downloads into the layout Jellyfin understands, without disturbing the originals.

```text
C:\qBittorrent\The Uprising 2026 1080p WEB-DL HEVC x265 5.1 BONE.mkv
  -> C:\Jellyfin Media\movies\The Uprising (2026)\The Uprising (2026).mkv

C:\qBittorrent\MobLand S02E05 Birdland 720p AMZN WEB-DL ... .mkv
  -> C:\Jellyfin Media\shows\MobLand\Season 02\MobLand S02E05.mkv
```

## How it decides

1. **Waits** for downloads that are still in progress (`.!qB`, `.!ut`, `.part`). On a full scan, it also waits for files changed in the last 10 minutes.
2. Finds the **main video**, skipping samples and anything under 20 MB.
3. **Episode or film?** If a file name contains `SxxEyy`, each such file is filed as an episode. A year in the show name, or in the folder name, is kept, e.g. `Marvel Zombies (2025)`. Otherwise the largest video is a film, named from the folder or file: the title before the first sensible year (skipping a year at the very start, or one in the future: *Blade Runner 2049 (2017)*).
4. **Subtitles:** keeps one English and one English SDH subtitle. A lone untagged subtitle is kept if it shares the video's name.
5. **Hard-links** each file to its tidy name: same data, no extra space, and qBittorrent keeps seeding. It falls back to copying across drives. If a destination already exists, it doesn't link.
6. Records the download in a **ledger**, so it is never filed again, even after you delete the tidy copy.

Anything it can't work out is left alone and logged once as **NEEDS YOU**.

## Usage

```text
MediaTidy.exe [--apply] [--source <dir>] [--dest <dir>] [<download path>]
```

| Example | Does |
| --- | --- |
| `MediaTidy.exe \| Out-Host` | Dry run over every download in `C:\qBittorrent` |
| `MediaTidy.exe --apply` | Files everything that's ready |
| `MediaTidy.exe --apply "%F"` | qBittorrent's *Run on torrent finished*: files that one download straight away |

Log: `%LOCALAPPDATA%\MediaTidy\mediatidy.log`. Ledger: `%LOCALAPPDATA%\MediaTidy\processed.json`.

## Build, test, install

```powershell
dotnet test
dotnet publish src/MediaTidy -c Release -o "$env:LOCALAPPDATA\MediaTidy\bin"
```

It's a Windows app (WinExe) so it never flashes a console window; pipe its output to see it. The tests use real release names, so add one for every new naming pattern you come across.
