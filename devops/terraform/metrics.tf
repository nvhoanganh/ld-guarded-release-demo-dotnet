# ─────────────────────────────────────────────────────────────────────────────
# Guarded-release metrics. These did NOT exist in LD before Terraform — the
# branch READMEs told you to create them by hand. Terraform now owns them.
#
# Two metrics, one per metric-ingestion style the branches demonstrate:
#
#   checkout-latency        — branch `no-otel-use-sdk-metric`. Fed directly by
#                             LdClient.Track("checkout-latency", ctx, null, ms).
#                             Pure SDK custom event, no OpenTelemetry.
#
#   http-latency-checkout   — branches `main`, `otel-integration`,
#                             `otel-integration-fan-out`. Wraps the Observability/
#                             OTel auto-generated event `http.latency;route=/api/checkout`
#                             into an LD-hosted custom metric you can attach to the
#                             Guarded Release.
#
# Both are numeric latency in ms, per request, lower-is-better — so a regression
# (latency climbing on the `true` variation) trips the auto-rollback.
# ─────────────────────────────────────────────────────────────────────────────

# Pure-SDK metric (Track API). Use as the monitored metric on the
# `no-otel-use-sdk-metric` branch.
resource "launchdarkly_metric" "checkout_latency" {
  project_key = var.ld_project_key
  key         = "checkout-latency"
  name        = "checkout-latency"
  description = "Checkout request latency in ms, emitted via LdClient.Track(\"checkout-latency\", ...). Custom numeric metric for the Guarded Release on the no-otel-use-sdk-metric branch. Lower is better."

  kind                         = "custom"
  event_key                    = "checkout-latency"
  is_numeric                   = true
  unit                         = "ms"
  analysis_type                = "mean"
  unit_aggregation_type        = "average"
  success_criteria             = "LowerThanBaseline"
  include_units_without_events = false
}

# Observability/OTel metric. Wraps the auto-generated per-route latency event.
# Use as the monitored metric on the main / otel-integration* branches.
resource "launchdarkly_metric" "http_latency_checkout" {
  project_key = var.ld_project_key
  key         = "http-latency-checkout"
  name        = "http-latency-checkout"
  description = "Average per-request latency (ms) for POST /api/checkout, from the LaunchDarkly Observability auto-generated event http.latency;route=/api/checkout. Monitored metric for the Guarded Release on the OTel/Observability branches. Lower is better."

  kind                         = "custom"
  event_key                    = "http.latency;route=/api/checkout"
  is_numeric                   = true
  unit                         = "ms"
  analysis_type                = "mean"
  unit_aggregation_type        = "average"
  success_criteria             = "LowerThanBaseline"
  include_units_without_events = false
}
