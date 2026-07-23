# ─────────────────────────────────────────────────────────────────────────────
# new-checkout-flow — the boolean flag every branch of this demo evaluates
# (Program.cs: ld.BoolVariation("new-checkout-flow", context, false)).
#
# false = old checkout (v1, fast + stable, 50–100ms).
# true  = new checkout (v2, slow + flaky, 300–800ms, ~20% HTTP 500).
#
# The Guarded Release is configured on this flag's default rule in the LD UI:
# serve a Guarded rollout targeting the `true` variation, monitored by the
# metrics in metrics.tf, with auto-rollback. LD rolls the flag back to `false`
# when the monitored metric regresses vs the control.
#
# This flag was created by hand first, then imported into Terraform state
# (see import.sh). The config below mirrors the live flag so `plan` is clean.
# ─────────────────────────────────────────────────────────────────────────────

resource "launchdarkly_feature_flag" "new_checkout_flow" {
  project_key = var.ld_project_key
  key         = "new-checkout-flow"
  name        = "new-checkout-flow"
  description = "Demo"

  variation_type = "boolean"

  variations = [
    {
      value = "true"
      name  = "true"
    },
    {
      value = "false"
      name  = "false"
    },
  ]

  # Off serves false (index 1) — the safe, pre-existing checkout path.
  defaults = {
    on_variation  = 0
    off_variation = 1
  }

  # Live flag has both SDK client-side reads enabled.
  client_side_availability = {
    using_environment_id = true
    using_mobile_key     = true
  }

  temporary = true
  tags      = ["demo"]
}

# Production targeting: flag ON, but the fallthrough (default rule) serves false.
# The Guarded rollout, once started in the UI, replaces this fallthrough with a
# staged rollout toward the `true` variation and manages rollback itself.
resource "launchdarkly_feature_flag_environment" "new_checkout_flow_production" {
  flag_id = launchdarkly_feature_flag.new_checkout_flow.id
  env_key = var.ld_environment_key

  on            = true
  off_variation = 1

  fallthrough = {
    variation = 1
  }
}
