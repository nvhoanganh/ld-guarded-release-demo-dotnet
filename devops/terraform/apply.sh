#!/bin/bash
# Plan + apply the guarded-release-demo LaunchDarkly flag + metrics.
#
# Shares the same LaunchDarkly account as the AVTA / AppleMusicPlaylist project,
# so the API key is read from that project's .env by default. Override by
# exporting LD_API_KEY, or by pointing LD_ENV_FILE at a different .env.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

LD_ENV_FILE="${LD_ENV_FILE:-$HOME/source/personal/AppleMusicPlaylist/server/.env}"

if [ -z "${LD_API_KEY:-}" ]; then
  if [ -f "$LD_ENV_FILE" ]; then
    LD_API_KEY=$(grep '^LD_API_KEY=' "$LD_ENV_FILE" | cut -d'=' -f2-)
  fi
fi

if [ -z "${LD_API_KEY:-}" ]; then
  echo "LD_API_KEY not set and not found in $LD_ENV_FILE" >&2
  echo "Export LD_API_KEY=... or set LD_ENV_FILE=/path/to/.env" >&2
  exit 1
fi

terraform init -upgrade
terraform plan  -var "ld_api_key=$LD_API_KEY"
terraform apply -var "ld_api_key=$LD_API_KEY"
