# AutoFactory — Recommendations for the Product Team

Deduped from 16 live-run findings (see `FINDINGS.md`). Each row = one product ask,
its owning team, priority, and the finding(s) it comes from.

**Priority:** **P0** = blocks production use · **P1** = needed for long-term use ·
**P2** = quality/reliability · **P3** = minor.

---

## Guarded Rollout / Release (LaunchDarkly core)

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P0** | **Configurable practical-significance / minimum-effect threshold** ("roll back only if >X% worse with confidence") | Today only `autoRollback: true/false`. With no effect-size tolerance, SEM→0 at scale means *any* real regression (+1ms) trips rollback → guarded rollout **over-triggers and is unusable at production volume**. *Biggest single ask.* | F14 |
| **P0** | **Per-metric rollback intent, exposed end-to-end** (gate vs monitor-only) | Common need: "roll back on errors, only *watch* latency." LD API already accepts per-metric `autoRollback`; it's dropped by manifest schema + Beacon before it reaches the release. | F15 |
| **P0** | **Distinguish "no data" from "no regression"** — zero samples in an arm → warn/hold, don't complete | A blind/empty metric completes a rollout identically to a genuinely safe one → "guarded" silently becomes "unguarded." | F1, F6 |
| **P1** | **Randomization unit must be an owned decision, never silently defaulted** | Unit (`user` vs `request`) changes power, stickiness, attribution — can't be inferred from a diff. Today it silently falls through to `user`. | F8 |

## Metrics / Observability

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P1** | **Dual-unit shared metrics** (analyze on both `user` + `request`) | Removes the reuse-vs-unit-match conflict — any rollout unit can then reuse a canonical shared metric instead of re-authoring. Verified healthy in LD UI. | F9 |
| **P2** | **SLO-shaped metric template** (count requests over a threshold / high percentile) | The real-world tolerance knob until F14 ships: a small in-SLO median shift doesn't register as a regression at all. | F14 |

## AutoFactory agents (customer-configurable AI Configs)

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P0** | **Deterministic post-check** — assert every manifest `metricKey` exists, receives data, and unit matches → fail/hold | Agents are non-deterministic: teaching the metrics-author improved it on one PR and it regressed on the next with the same config. **The only reliable guard.** | F4, F13 |
| **P1** | **Fixed policy for the guardrail metric** where a canonical one exists; agent *adds* diagnostics, doesn't *pick* the gate | Takes the safety-critical choice off the non-deterministic sampler. | F13 |
| **P2** | **Metric-intent classification** (guardrail / diagnostic / conversion); require ≥1 real guardrail that measures harm + fires on both arms | Two of three agent-authored metrics were structurally incapable of detecting the regression. | F2 |
| **P2** | **Prefer Observability event metrics over unvalidated trace queries** | The one correct metric was a hand-written trace query with a wrong span name → 0 data all release. | F3 |
| ✅ Done | **Explicit cleanup-aware gate in research-planner** (title/branch/diff-shape) | Validated live this session: cleanup PR classified `skip_flagging=true`, no re-flag. Decision lives in the factory control plane, not per-repo CI YAML. | F11 |

## Phase 3 / Cleanup

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P1** | **Build Phase 3 cleanup** — terminal release → agent opens cleanup PR (inline winner / delete loser, remove flag), code-refs-guided | Mirror of Phase 1. Today N PRs → N dead flag blocks; no trigger, no agent, no sweep. Single biggest missing piece for long-term use. | F10 |
| **P1** | **Cleanup must extend to the metric layer** — strip release-scoped `Track` calls + archive orphaned metrics (reference-gated) | Vega retires the flag but leaves the `Track` emitting and the metrics in LD → N releases → N orphaned metrics. **Vega is LD-managed/uncontrollable** — owner is a factory companion agent, a Beacon post-merge hook, or the LD Vega team. | F16 |
| **P1** | **Metrics-author tags release-scoped metrics at creation** (a **tag** like `release-scoped` linked to the flag key — **not** a `temp-xx` name prefix) so cleanup can find deletion candidates | Gives the janitor a cheap filter: `metrics where tag=release-scoped AND flag=<retired> AND refs=0 → archive`, plus an audit trail of which release created which metric. **Caveats:** (1) name prefix is wrong — the key is permanent, pollutes dashboards, blocks reuse; use a tag. (2) tag = *candidate*, not *safe-to-delete* — still **reference-gate** (0 attachments + 0 code-refs), or you kill a metric feeding another live rollout (F9 shared/dual-unit reuse). (3) Shared metrics (`http-latency-checkout`) get **no** tag → never auto-deleted. (4) **Vega can't consume a tag you invent** — the consumer is your janitor / Beacon hook, not Vega. | F16 |
| **P1** | **Hold on *reverted* releases; auto-clean only *completed* ones; never push a cleanup that fails to build** | A reverted flag ("v1 regressed, fix & retry") looks identical to an abandoned one — cleanup deleted a whole feature and pushed an uncompilable build. | F12 |

## Manifest / release-plan tooling

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P1** | **Assisted, schema-validated manifest authoring** — no hand-editing raw JSON | Correcting a release meant a human hand-editing `.release-flags/*.json` (metric keys, unit, method) with no validation — exactly where new mistakes enter. | F5 |

## Beacon

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P0** | **Stop flattening all metrics to `autoRollback: true`** — honor the agent's per-metric role (`killswitch` / `monitoring` / `pause`) | The agent already classifies intent; `trigger.ts` overwrites it so even monitor-only metrics force a revert. Enables F15. | F15 |

## Agent runtime (minor)

| Pri | Recommendation | Why | Findings |
|---|---|---|---|
| **P3** | **Sandbox/timeout the agent shell for .NET; treat test-node failure as non-blocking for already-produced artifacts** | `flag-testing` hung ~16 min on a `dotnet` process holding file descriptors → red run, even though every artifact landed and was judge-verified first. | F7 |

---

## The three that matter most

1. **P0 — Practical-significance threshold** (F14). Without it, guarded rollout is unusable at real volume.
2. **P0 — Deterministic metric post-check** (F4/F13). The only reliable guard against a non-deterministic agent shipping a blind guardrail.
3. **P0 — Per-metric rollback intent, end-to-end** (F15, incl. Beacon un-flattening). Turns "every metric gates" into "gate on what matters, watch the rest."

**What already works** (don't re-litigate): the orchestration (PR → flag → wrap → metrics → deploy → guarded release → auto-rollback) is fully automated and correct; the improvement loop is real (fix an agent as config, verified on the next PR); Observability telemetry needed zero extra app instrumentation.
