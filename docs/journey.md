# How a film gets to the TV

The whole journey on one page. The numbers on the arrows are the order things happen. For the details behind each step, see [media-server.md](media-server.md).

```mermaid
flowchart LR
    subgraph PHONE["📱 Phone"]
        APP_W["Watchlist app"]
    end

    subgraph NET["☁️ Internet"]
        CF["Cloudflare<br/>login + tunnel"]
        IA[("Internet Archive<br/>legal films")]
        TMDB[("TMDb / OMDb<br/>posters, details")]
    end

    subgraph PI["🍓 Raspberry Pi 5"]
        subgraph WLC["watchlist container"]
            WL["Watchlist"]
        end
        subgraph ARR["*arr stack · /opt/arr"]
            RS["Radarr (films)<br/>Sonarr (TV)"]
            PR["Prowlarr"]
            QB["qBittorrent"]
        end
        subgraph DISK["storage · /srv/data"]
            TOR["torrents/"]
            LIB["library/<br/>movies, shows"]
        end
        subgraph JFC["jellyfin container"]
            JF["Jellyfin"]
        end
    end

    subgraph TV["📺 Sony TV"]
        APP_J["Jellyfin app"]
    end

    APP_W -- "1. ask for a film" --> CF --> WL
    WL -- "2. add to wanted list" --> RS
    RS -- "3. search" --> PR -- "3. search" --> IA
    RS -- "4. send best match" --> QB
    IA -- "5. download" --> QB --> TOR
    TOR -- "6. file it, remove torrent" --> LIB
    LIB -- "7. spot new file" --> JF -- "7. fetch artwork" --> TMDB
    RS -. "8. it's in" .-> WL -. "8. push alert" .-> APP_W
    APP_J -- "9. browse and play<br/>home Wi-Fi only" --> JF
```

| # | What happens |
| --- | --- |
| 1 | You name a film or a show in the watchlist chat on your phone. Cloudflare checks it's you before the request reaches the Pi. |
| 2 | The watchlist hands films to **Radarr** and shows (the whole series) to **Sonarr**. |
| 3 | Radarr or Sonarr searches through **Prowlarr**, which asks the Internet Archive (legal sources only). |
| 4 | The best match goes to **qBittorrent**. |
| 5 | qBittorrent downloads it into `/srv/data/torrents`, and stops when it's complete (no seeding). |
| 6 | Radarr or Sonarr gives it its proper name in `/srv/data/library`, then removes the torrent, so the library file is the only copy. |
| 7 | **Jellyfin** sees the new file within seconds and fetches its poster and details. |
| 8 | Radarr or Sonarr tells the watchlist, which sends a push alert to your phone. |
| 9 | You open the Jellyfin app on the TV and play it. This only works on the home Wi-Fi. |

**When you're done:** delete it in Jellyfin. The space is freed straight away, and it isn't downloaded again.
