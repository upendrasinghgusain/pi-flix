# Runtime only. The app is published on the laptop for linux-arm64 into ./app (see
# services/watchlist/deploy.sh). "extra" adds ICU and time zone data, needed for Europe/London.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra
WORKDIR /app
COPY app/ ./
ENV ASPNETCORE_URLS=http://127.0.0.1:8190 \
    Watchlist__DataDir=/data
ENTRYPOINT ["dotnet", "Watchlist.dll"]
