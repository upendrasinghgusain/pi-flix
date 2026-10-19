# Watchlist

The chat agent, list and release checker described in [docs/watchlist.md](../../docs/watchlist.md).

```powershell
cd services/watchlist
dotnet test
dotnet run --project src/Watchlist --launch-profile Watchlist   # http://127.0.0.1:8190, Access check off
```

For the chat and voice to work locally, set the keys as user secrets or environment variables, using the names in [pi/watchlist/.env.example](../../pi/watchlist/.env.example) (e.g. `$env:OpenAI__ApiKey = "..."`).

## Deploy

```bash
services/watchlist/deploy.sh    # Git Bash: test, publish for arm64, copy to /opt/watchlist, restart
```

On the Pi, `/opt/watchlist` holds `compose.yaml`, `Dockerfile`, `app/` (the published build), `.env` (secrets, mode 600) and `data/watchlist.db`.

## Layout

| File | Does |
| --- | --- |
| `Agent/ChatAgent.cs` | The tool-calling loop and the system prompt |
| `Agent/WatchlistTools.cs` | The tools the model can call: TMDb search, watchlist changes and explicit TV download requests |
| `Availability.cs` | The rules for *available*: watchable at home anywhere in the world |
| `Tmdb/TmdbClient.cs` | Search, details, release dates and watch providers |
| `Access/CloudflareAccess.cs` | Checks Cloudflare Access's signed token on every request |
| `Access/Hooks.cs` | Checks the password on Radarr's webhook calls |
| `Alerts/` | The alert rules, and the background job: daily check, pushes, and the pipeline check every 15 minutes |
| `Radarr/` | Sends films to Radarr, follows them by webhook, deletes files; the daily catch-up |
| `Sonarr/` | Sends an explicitly requested series, season or episode to Sonarr for monitoring and search |
| `Pipeline/` | The Status tab: reads Prowlarr, Radarr and Sonarr, classifies indexer failures, alerts on anything broken |
| `Push/` | Web Push: signing keys and sending |
| `Data/` | SQLite: items, chat history, alerts, push devices, meta |
| `wwwroot/` | The installable web app: chat, list and Status |
