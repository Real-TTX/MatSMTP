#!/usr/bin/env bash
# Rebuilds the MatSMTP container and redeploys the dev stack (live-reload / test workflow).
#   ./scripts/redeploy.sh          rebuild + recreate, keep data
#   ./scripts/redeploy.sh --fresh  also delete the data volume
set -euo pipefail
cd "$(dirname "$0")/.."

export MATSMTP_CHANNEL=local
export MATSMTP_BUILD_DATE="$(date -u +%Y%m%d)"

if [[ "${1:-}" == "--fresh" ]]; then
  docker compose -f docker-compose.dev.yml down -v
fi

docker compose -f docker-compose.dev.yml up -d --build --remove-orphans
echo
echo "MatSMTP local-${MATSMTP_BUILD_DATE} is running on http://localhost:2525"
