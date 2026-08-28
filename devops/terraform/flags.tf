# Boolean flag gating the "new checkout flow" in POST /api/checkout.
# Evaluated in Program.cs via ld.BoolVariation("new-checkout-flow", context, false)
# with a multi-context (user kind = request body userId, request kind =
# HttpContext.TraceIdentifier). The Guarded Release rollout on this flag is
# configured in the LD UI (see README) — the provider does not manage
# guarded rollout stages.
resource "launchdarkly_feature_flag" "new_checkout_flow" {
  project_key = var.ld_project_key
  key         = "new-checkout-flow"
  name        = "New Checkout Flow"
  description = "Serves the v2 checkout engine (deliberately slower + ~20% error rate) so a Guarded Release can detect the regression and auto-roll back"

  variation_type = "boolean"

  variations = [
    {
      value = true
      name  = "New flow (v2)"
    },
    {
      value = false
      name  = "Old flow (v1)"
    },
  ]

  defaults = {
    on_variation  = 0
    off_variation = 1
  }

  temporary = true
  tags      = ["backend", "dotnet", "guarded-release", "checkout"]

  client_side_availability = {
    using_environment_id = false
    using_mobile_key     = false
  }
}

# Environment targeting: flag off, falling through to the old flow.
# Flip on / attach the Guarded rollout in the LD UI when running the demo.
resource "launchdarkly_feature_flag_environment" "new_checkout_flow_env" {
  flag_id = launchdarkly_feature_flag.new_checkout_flow.id
  env_key = var.ld_environment_key

  on = false

  fallthrough = {
    variation = 1 # Old flow (v1)
  }

  off_variation = 1
}
