# Roadmap

Roughly in the order worth doing. Each item says why it matters and what is already known.

## 1. Ethernet for the Pi

**Why:** copying from the laptop ran at about 5 MB/s, even though both the Pi and the laptop have a strong 5 GHz signal (the Pi at -36 dBm and 540 Mbit/s). The limits are the Pi's small Wi-Fi chip and both machines sharing the air. With the Pi on a cable, expect 25–40 MB/s, at which point storage becomes the limit instead.

**Options, best first:**
1. A flat Cat6 cable along skirting boards or under the carpet (fine up to 100 m).
2. A mesh unit with an Ethernet port near the Pi.
3. Powerline adapters, plugged directly into the wall (10–40 MB/s, depending on the wiring).
4. Moving the Pi next to the router.

**Then:** prefer Ethernet over Wi-Fi on the Pi, and keep the same address.

## 2. A fixed address

The TV, the laptop (SSH) and the docs all use `192.168.1.40`. Add a **DHCP reservation** for the Pi in the router (for its Ethernet connection too, once it has one), so the address never changes.

## 3. The 1 TB drive as `/srv/data`

**Why:** takes the films and the wear off the SD card. The SD card has about 87 GB free (2026-10-18) and writes at 28 MB/s.

**Plan:**
1. Plug the drive into a blue USB 3 port.
2. Check its health (SMART), since it's an older drive.
3. Format it ext4 with the label `media`.
4. Stop the *arr stack, Jellyfin and the watchlist; copy `/srv/data` across with `rsync -aH` (keeps any hard links; normally there are none, since nothing seeds and finished torrents are removed).
5. Mount it at `/srv/data` at boot, by label, with `nofail`.

The library (`library/`) and the downloads (`torrents/`) must stay on the same drive, or Radarr's imports become full copies (twice the space until the torrent is removed). Jellyfin and the *arr stack need no changes. A later, larger drive is swapped in the same way. The Pi's 5 A power supply can power portable drives over USB (1.6 A).

## 4. Run the whole Pi from NVMe

**Why:** the operating system, Docker, the risky-business prod database and its backups all live on the SD card, the Pi's weakest part. An NVMe drive is faster and far more reliable.

**Needs:**
- an M.2 HAT (the official HAT+ takes 2230/2242 drives; the Pimoroni NVMe Base also takes 2280);
- a 1–2 TB NVMe drive;
- a check that your case has room.

**Downtime is acceptable** (risky-business can be offline), so it can be done in one go:
1. Stop the services.
2. Copy the SD card to the NVMe.
3. Change the boot order.
4. Start up and check everything.
5. Keep the SD card as a fallback.

## 5. Backups that leave the Pi

**Why:** risky-business's prod backups, Jellyfin's settings, the watchlist's database (`/opt/watchlist/data`), the *arr settings (`/opt/arr`) and, later, photos and documents all exist only on the Pi. Follow the 3-2-1 rule: three copies, on two kinds of storage, one away from the house.

**Options:** a second USB drive plus a cloud copy (e.g. Backblaze B2 or a similar storage bucket, encrypted with restic). Radarr's and Sonarr's downloads don't need backing up (they can be fetched again); films you copied in yourself exist only on the Pi.

## 6. Remote access with Tailscale

**Why:** private access to Jellyfin, and later photos and documents, from anywhere, with nothing exposed to the internet. Phone apps keep working.

**Ruled out:**
- **Cloudflare Tunnel for video:** its terms forbid it, and the tunnel also serves the website and risky-business. It also has a 100 MB upload limit on the free plan, and the Access login page blocks the apps.
- **A direct subdomain with port forwarding:** possible, but it exposes the Pi and your home IP.

Jellyfin's `EnableRemoteAccess` setting would need switching back on for Tailscale addresses.

## 7. Photos and documents

- **Immich** for photos and videos: phone backup, a timeline, face search.
- **Nextcloud** (everything) or **Seafile** (fast file syncing) for documents.

These hold irreplaceable data, so do items 3–5 first. Reach them with Tailscale. Keep Cloudflare subdomains for small things shared by link.

## 8. Watchlist: what's left

Done: the chat app, release alerts, downloading through Radarr, explicit series/season/episode requests through Sonarr, and the Status tab ([watchlist.md](watchlist.md)). Still to do:
- **"In your library" from Jellyfin**, so TV and films you copied in yourself count too (today only Radarr's films do); a Jellyfin API key goes in its `.env`.
- Weekly digest, a "Hey Google" shortcut, the MCP server, auto-*watched* from Jellyfin plays.

## Housekeeping

- Add the *arr cron lines to upendra's crontab on the Pi (`crontab -e`, no sudo); see [media-server.md](media-server.md#the-arr-pipeline-public-domain-titles):
  - `*/5 * * * * python3 /opt/arr/unstick.py --apply >> /opt/arr/unstick.log 2>&1` (needed: Internet Archive downloads can stick at 99%);
  - `17 * * * * python3 /opt/arr/reclaim.py --apply >> /opt/arr/reclaim.log 2>&1` (safety net).
- Regenerate Prowlarr's API key (part of it was printed in a session on 2026-10-18): Prowlarr → Settings → General, then `sudo python3 /opt/arr/setup.py` and restart the watchlist.
- Delete *Animals (2026)* from the library: it came from The Pirate Bay before that indexer was removed.
- Remove `/etc/sudoers.d/010-upendra-nopasswd` from the Pi. It was added for the initial setup.
- Change the Pi password. It was shared in a chat during setup.
- Create a non-admin Jellyfin user for the TV (with *Allow media deletion* if you want to delete from the TV).
- Check the router: UPnP off, IPv6 firewall on.
- Consider a guest Wi-Fi for visitors and smart gadgets.
