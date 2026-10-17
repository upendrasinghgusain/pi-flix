# Roadmap

Roughly in the order worth doing. Each item says why it matters and what is already known.

## 1. Ethernet for the Pi

**Why:** syncing runs at about 5 MB/s, even though both the Pi and the laptop have a strong 5 GHz signal (the Pi at -36 dBm and 540 Mbit/s). The limits are the Pi's small Wi-Fi chip and both machines sharing the air. With the Pi on a cable, expect 25–40 MB/s, at which point storage becomes the limit instead.

**Options, best first:**
1. A flat Cat6 cable along skirting boards or under the carpet (fine up to 100 m).
2. A mesh unit with an Ethernet port near the Pi.
3. Powerline adapters, plugged directly into the wall (10–40 MB/s, depending on the wiring).
4. Moving the Pi next to the router.

**Then:** prefer Ethernet over Wi-Fi on the Pi, and keep the same address.

## 2. A fixed address

The TV, the laptop's Syncthing and the docs all use `192.168.1.40`. Add a **DHCP reservation** for the Pi in the router (for its Ethernet connection too, once it has one), so the address never changes.

## 3. The 1 TB drive as `/srv/media`

**Why:** takes the films and the wear off the SD card. The SD card has about 90 GB free and writes at 28 MB/s.

**Plan:**
1. Plug the drive into a blue USB 3 port.
2. Check its health (SMART), since it's an older drive.
3. Format it ext4 with the label `media`.
4. Copy `/srv/media` across.
5. Mount it at `/srv/media` at boot, by label, with `nofail`.

Jellyfin and Syncthing need no changes. A later, larger drive is swapped in the same way. The Pi's 5 A power supply can power portable drives over USB (1.6 A).

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

**Why:** risky-business's prod backups, Jellyfin's settings and, later, photos and documents all exist only on the Pi. Follow the 3-2-1 rule: three copies, on two kinds of storage, one away from the house.

**Options:** a second USB drive plus a cloud copy (e.g. Backblaze B2 or a similar storage bucket, encrypted with restic). Films don't need backing up: the laptop holds the master copies.

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

## 8. Watchlist release tracker

**Why:** finding what to watch, and knowing when it's out, takes the most time.

**Design:** see [watchlist.md](watchlist.md). In short:
- **The chat app:** a web app you install to your phone, with voice, behind a Cloudflare Access login. An OpenAI agent turns rough descriptions into the right title and saves it.
- **The release checker:** watches each saved title for UK dates, UK streaming services and your Jellyfin library, and sends push alerts when something changes. For example: "*Weapons* is now on Prime", or "out to rent today".

**Scope:** it tracks and notifies only. It doesn't search for or download anything.

## Housekeeping

- Remove `/etc/sudoers.d/010-upendra-nopasswd` from the Pi. It was added for the initial setup.
- Change the Pi password. It was shared in a chat during setup.
- Create a non-admin Jellyfin user for the TV.
- Check the router: UPnP off, IPv6 firewall on.
- Consider a guest Wi-Fi for visitors and smart gadgets.
- After `winget upgrade Syncthing.Syncthing`, point the *Syncthing* scheduled task at the new versioned folder.
