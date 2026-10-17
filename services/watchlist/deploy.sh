#!/usr/bin/env bash
# Tests, publishes for the Pi, copies to /opt/watchlist and restarts the container.
# Run from Git Bash on the laptop. /opt/watchlist/.env must already exist on the Pi.
set -euo pipefail
cd "$(dirname "$0")"

SSH=("/c/Program Files/Git/usr/bin/ssh.exe" -o BatchMode=yes -o HostKeyAlias=pieasypieces)
SCP=("/c/Program Files/Git/usr/bin/scp.exe" -o BatchMode=yes -o HostKeyAlias=pieasypieces)
PI=upendra@192.168.1.40

dotnet test --nologo -v q
rm -rf out
dotnet publish src/Watchlist -c Release -r linux-arm64 --self-contained false -o out/app --nologo -v q

"${SSH[@]}" "$PI" 'test -f /opt/watchlist/.env || { echo "Create /opt/watchlist/.env first"; exit 1; }
                   rm -rf /opt/watchlist/app && mkdir -p /opt/watchlist/data'
"${SCP[@]}" -rq out/app ../../pi/watchlist/compose.yaml ../../pi/watchlist/Dockerfile "$PI:/opt/watchlist/"
"${SSH[@]}" "$PI" 'cd /opt/watchlist && sudo docker compose up -d --build --quiet-pull 2>&1 | tail -3
                   for i in 1 2 3 4 5 6 7 8 9 10; do curl -fsS http://127.0.0.1:8190/health && echo " healthy" && exit 0; sleep 2; done
                   sudo docker logs --tail 30 watchlist; exit 1'
