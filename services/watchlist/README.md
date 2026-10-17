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
| `Agent/WatchlistTools.cs` | The tools the model can call: TMDb search and watchlist changes only |
| `Availability.cs` | The rules for *available*: watchable at home anywhere in the world |
| `Tmdb/TmdbClient.cs` | Search, details, release dates and watch providers |
| `Access/CloudflareAccess.cs` | Checks Cloudflare Access's signed token on every request |
| `Data/` | SQLite: items and chat history |
| `wwwroot/` | The installable web app: chat and list |
