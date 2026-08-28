#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

ENV_FILE="$SCRIPT_DIR/../../.env"

if [[ -z "${LD_API_KEY:-}" && -f "$ENV_FILE" ]]; then
  LD_API_KEY=$(grep '^LD_API_KEY=' "$ENV_FILE" | head -1 | cut -d'=' -f2-)
fi

: "${LD_API_KEY:?Error: LD_API_KEY not set in .env or environment}"

# Passed as an env var, not -var, so the token never appears in `ps` output.
export TF_VAR_ld_api_key="$LD_API_KEY"

terraform init -upgrade
terraform plan
terraform apply
