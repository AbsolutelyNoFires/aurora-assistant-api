#!/bin/bash
# Build + deploy the patch, restart Aurora (skipping the AuroraPatch launcher),
# and wait for the API. Unsaved game progress is lost on restart.
# Assumes the Linux/Proton setup with ~/.local/bin/aurora as the game launcher.
set -euo pipefail
cd "$(dirname "$0")/.."
PORT="${PORT:-47100}"
PATTERN='umu-run .*AuroraPatch\.exe|[A-Z]:\\.*Aurora(Patch)?\.exe'

dotnet build src/AuroraAssistantApi -c Release -t:Deploy -v q -nologo

if pgrep -f "$PATTERN" >/dev/null; then
    pkill -f "$PATTERN" || true
    for _ in $(seq 30); do pgrep -f "$PATTERN" >/dev/null || break; sleep 1; done
fi

DISPLAY="${DISPLAY:-:1}" setsid nohup "$HOME/.local/bin/aurora" -launch >/dev/null 2>&1 &

for _ in $(seq 180); do
    if curl -sf "http://127.0.0.1:$PORT/health"; then echo; exit 0; fi
    sleep 1
done
echo "API did not come up; see AuroraPatch.log in the Aurora folder" >&2
exit 1
