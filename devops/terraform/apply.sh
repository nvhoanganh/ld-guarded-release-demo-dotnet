#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

ENV_FILE="$SCRIPT_DIR/../../.env"

if [[ -z "${LD_API_KEY:-}" && -f "$ENV_FILE" ]]; then
  LD_API_KEY=$(grep '^LD_API_KEY=' "$ENV_FILE" | cut -d'=' -f2-)
fi

LD_API_KEY="${LD_API_KEY:?Error: LD_API_KEY not set in .env or environment}"

terraform init -upgrade
terraform plan -var "ld_api_key=$LD_API_KEY"
terraform apply -var "ld_api_key=$LD_API_KEY"
