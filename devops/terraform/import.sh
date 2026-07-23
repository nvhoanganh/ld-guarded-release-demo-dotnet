#!/bin/bash
# One-time: pull the hand-created LaunchDarkly resources into Terraform state
# so Terraform manages them instead of trying to re-create them.
#
# All three pre-exist (created in the LD UI): the `new-checkout-flow` flag and
# both guarded-release metrics (`checkout-latency`, `http-latency-checkout`).
#
# Safe to re-run: `terraform import` is a no-op if the resource is already in state.
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
  exit 1
fi

PROJECT="${LD_PROJECT_KEY:-default}"
ENVIRONMENT="${LD_ENVIRONMENT_KEY:-production}"

terraform init -upgrade

# Flag resource id: <project>/<flagKey>
terraform import \
  -var "ld_api_key=$LD_API_KEY" \
  launchdarkly_feature_flag.new_checkout_flow \
  "${PROJECT}/new-checkout-flow"

# Per-environment config id: <project>/<env>/<flagKey>
terraform import \
  -var "ld_api_key=$LD_API_KEY" \
  launchdarkly_feature_flag_environment.new_checkout_flow_production \
  "${PROJECT}/${ENVIRONMENT}/new-checkout-flow"

# Metric resource id: <project>/<metricKey>
terraform import \
  -var "ld_api_key=$LD_API_KEY" \
  launchdarkly_metric.checkout_latency \
  "${PROJECT}/checkout-latency"

terraform import \
  -var "ld_api_key=$LD_API_KEY" \
  launchdarkly_metric.http_latency_checkout \
  "${PROJECT}/http-latency-checkout"

echo
echo "Imported. Now run:  terraform plan -var \"ld_api_key=\$LD_API_KEY\""
echo "A clean plan (metrics to add, flag unchanged) means state is in sync."
