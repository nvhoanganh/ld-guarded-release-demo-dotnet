# Metric driving the Guarded Release on new-checkout-flow.
#
# The event key is emitted automatically by the LaunchDarkly Observability
# plugin (Program.cs -> ObservabilityPlugin) from the ASP.NET Core server span
# for POST /api/checkout — no ldClient.Track() call in app code.
#
# The event key only appears in LD after the app has sent trace data for ~30s.
# If terraform apply fails on an unknown event key, run the app first.
resource "launchdarkly_metric" "http_latency_checkout" {
  project_key = var.ld_project_key
  key         = "http-latency-checkout"
  name        = "HTTP latency — /api/checkout"
  description = "Average server-side latency (ms) for POST /api/checkout, derived from OpenTelemetry spans ingested by the LD Observability module."

  kind      = "custom"
  event_key = "http.latency;route=/api/checkout"

  is_numeric = true
  unit       = "ms"

  success_criteria = "LowerThanBaseline"
  analysis_units   = ["request"]

  tags = ["checkout", "performance", "guarded-release"]
}
