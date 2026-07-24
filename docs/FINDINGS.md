# AutoFactory — Live Demo Findings & Limitations

Findings from running the AutoFactory pipeline **end-to-end against a live app**
(this repo's .NET checkout service on Railway, Beacon on Railway, LaunchDarkly
`default` project, Cursor as the Phase 1 agent provider).

The pipeline works — a pull request became a feature-flagged, metric-instrumented
change, and Beacon started a guarded release on deploy with no human in the
critical path. But the run also surfaced concrete limitations worth showing a
design partner. **The headline: on the first live release, a genuine 5× latency
regression was promoted to 100% of traffic** — because the guardrail metric the
agent chose was blind to it.

> Scope note: these are prototype findings, not product defects. Each has a
> clear, fixable remediation. They are documented here precisely because the
> honest failure modes are the useful part of a design-partner evaluation.

---

## The scenario we ran

- **Change:** a new `EnrichCheckout` step on `POST /api/checkout` that adds
  ~220–320 ms (a "recommendations model" call).
- **Phase 1 (agents, in CI):** created flag `enable-checkout-recommendations`
  (multivariate `control`/`v1`, created dark), wrapped the code fail-safe to
  `control`, authored three metrics, and wrote the release manifest
  `.release-flags/pr-1.json`. Judges scored the flag-implementer 0.92 and the
  metrics-author 0.85.
- **Phase 2 (Beacon):** on deploy-notify, discovered the manifest and started a
  guarded release (v1 20% → 50%), monitoring the manifest's metrics.
- **Load:** k6 drove `/api/checkout`; ~20% of traffic hit v1 (~360 ms) vs
  control (~70 ms) — a clean, dramatic regression.

---

## Finding 1 — A broken release rolled *forward* to 100%

On the release monitoring the **agent-authored** metrics, LaunchDarkly saw **no
regression** and **completed the rollout to 100% v1** — shipping the slow path to
all users. Not because the change was safe, but because **none of the three
monitored metrics could detect the harm.**

Only after repointing the guarded release at a *different* metric
(`http-latency-checkout`, which actually measures request latency) did the same
change **auto-roll-back in ~83 seconds** on the regression.

**This is the core risk:** a guarded release is only as safe as the metric it
watches. A blind metric turns "guarded" into "unguarded" — and the rollout's
*success* (promote to 100%) is the dangerous outcome, not its failure.

---

## Finding 2 — The agent's metric *selection* was wrong for the risk

The metrics-author instrumented three metrics. Measured against the actual harm
(latency), here is what each one does:

| Metric | Fires when | Measures | Detects the slow v1? |
|---|---|---|---|
| `…-error` | only inside the v1 `catch` — **v1-only and failure-only** | enrichment threw an exception | **No** — enrichment never throws → ~0 samples |
| `…-business` | on every checkout (**both arms**) | "a checkout happened" (a count) | **No** — increments equally on control and v1 → no difference |
| `…-latency` | every `/api/checkout` | request duration — **the real harm** | **Only correct one** — but implemented as a broken `trace` query (see Finding 3) |

The instinct — "instrument latency + error + business" — is reasonable, but the
agent did not distinguish:

- **Guardrail metrics** (must measure the harm *and* fire on both arms so a
  comparison exists) from
- **Activity counters** (`…-business` — counts an event, can't express a
  regression) and **rare-event metrics** (`…-error` — near-zero data here).

So even with a *working* latency metric, two of the three guardrails were
structurally incapable of flagging a latency regression.

**Remediation:** teach the metrics-author to classify metric intent
(guardrail vs. diagnostic vs. conversion) and require at least one guardrail
that (a) measures the change's most likely harm and (b) is emitted on both the
control and treatment paths.

---

## Finding 3 — The one right metric was implemented incorrectly

The `…-latency` metric was created as a **`trace` metric** with the query:

```
service_name=guarded-release-demo AND span_name=POST /api/checkout → duration
```

It returned **0 data** for the entire release. Meanwhile the app's Observability
plugin *was* emitting perfectly good spans — the auto-generated event
`http.latency;route=/api/checkout` carried full context (user, request, route)
and the real per-request latency (control ~62 ms, v1 ~383 ms). The metric that
reads that event (`http-latency-checkout`) worked immediately.

The agent chose the more fragile path (a hand-written trace query that had to
match an exact span name) over the reliable, already-populated Observability
event metric — and got the query wrong.

**Remediation:** prefer proven Observability *event* metrics over trace metrics
with unverified queries; if a trace metric is used, validate its query against
the actual span names the service emits before trusting it.

---

## Finding 4 — The review layer checks the *diff*, not the *data flow*

The judges scored the metrics-author **0.85** and the reviewer passed the change.
Both validate the **code diff** — "does the response honestly reflect what the
agent claims?" Neither asks the operational question: **"will any of these
metrics actually receive data, and can they detect the regression this change
risks?"**

A metric can be created, look correct in review, compile, and still be silently
empty at runtime. Nothing in the pipeline catches that.

**Remediation:** add a runtime data-flow check (see below), and extend the judge
rubric to include "would this metric detect the plausible harm of this change?"

---

## Finding 5 — The manifest is hand-edited JSON, which invites mistakes

Correcting the release required a **human hand-editing `.release-flags/pr-1.json`**
— swapping `metricKeys`, changing `randomizationUnit` from `user` to `request`,
adding `releaseMethod: guarded`. This is raw JSON with:

- no schema validation at author time (a typo'd metric key fails silently at
  release, or worse, produces a blind guardrail),
- no awareness of which metrics exist or which are valid guardrails,
- no link between `randomizationUnit` and the chosen metric's unit (a mismatch
  produces attribution problems), and
- no guardrails against removing the wrong field.

Hand-editing the release plan is **not a designed step** — the `releasePlan`
block is meant to be machine-owned; the human-owned part is only `releaseIntent`
(`auto`/`hold`/`manual`). But when the agent gets the release plan wrong, the
only recovery today is a human editing JSON by hand, which is exactly where new
mistakes get introduced.

**Remediation:** provide an assisted authoring path for the release plan —
a validated form / CLI that lists the project's real metrics, flags non-guardrail
choices, enforces `randomizationUnit`↔metric-unit consistency, and validates
against the manifest schema before commit. Humans should never hand-edit this
JSON.

---

## Finding 6 — LaunchDarkly conflates "no data" with "no regression"

Across two runs we saw both revert reasons:

- **Run A** reverted after ~20 minutes with *"insufficient sample size — retry
  with a longer duration"* (load didn't overlap the stage window).
- **Run C** reverted in ~83 seconds on the real regression.

But a guarded rollout that **completes** because its metric is empty (Finding 1)
looks identical to one that completes because the change is genuinely safe. The
platform surfaces "insufficient samples" as a revert reason, but does not
distinguish **"this metric received zero events (it's broken/misrouted)"** from
**"this metric is healthy and shows no regression."**

**Remediation (in-flight check):** during the first stage, if a monitored metric
has **zero samples in *either* arm** after a short window, treat that as
*"metric not wired"* and **warn/hold**, rather than allowing the rollout to
complete. This is the fallback for metrics that can't be pre-flighted.

---

## Finding 7 (minor) — The flag-testing agent hung on .NET, failing the run

The final Phase 1 node (`flag-testing`) ran a `dotnet` command that spawned a
long-lived process the Cursor SDK's shell tool couldn't detect as exited
(*"background process holding file descriptors open"*). It hung ~16 minutes,
then crashed with exit code 1 — so the **GitHub Action shows red**, even though
every release artifact (flag, wrapped code, metrics, manifest) landed and was
judge-verified *before* the failure.

**Remediation:** sandbox/timeout the agent's shell for .NET (never start a
blocking server or a test host that doesn't return); treat a test-node failure
as non-blocking for the already-produced artifacts.

---

## Can a runtime metric check even work if the metric is on the dark path?

A fair objection: if a metric only fires on the new (dark) code, you can't verify
it before the release. True — but that class of metric is a **bad guardrail
anyway**:

- **Dark-path-only metrics** (e.g. an event tracked *only* inside `v1`) have **no
  control baseline** to compare against, so they cannot express a regression.
  They are diagnostics, not guardrails — and yes, they can't be pre-checked.
- **Valid guardrail metrics** measure the **shared path** (every request,
  control and treatment alike — e.g. endpoint latency). Those are **already
  flowing from existing/control traffic before the release**, so a **pre-flight
  check works** for exactly the metrics that matter.

So the pre-flight check is possible *because* the only sensible guardrails are
shared-path metrics. The in-flight "zero samples = broken, not healthy" check
(Finding 6) covers the remainder.

---

## What worked (for balance)

- Flag creation, code wrapping (fail-safe to `control`), manifest authoring, and
  the deploy → Beacon → guarded-release → rollback orchestration were **fully
  automated and correct**.
- The app's **Observability telemetry was excellent** — spans carried user +
  request + route + latency, enabling a working guardrail metric with **zero
  extra app instrumentation**.
- Once pointed at the right metric, the guarded release **detected a 5×
  regression and auto-rolled-back in ~83 seconds** — the intended behavior,
  exactly as designed.
- The agents **disclosed their assumptions** (e.g. the flag-implementer's commit
  noted "clients should tolerate a null/missing field"), giving the human gate
  real signal to review.

---

## Summary of remediations

| # | Finding | Fix |
|---|---|---|
| 1 | Broken release rolled forward to 100% | The rest of this table; a guarded release is only as safe as its metric |
| 2 | Wrong metric *selection* | Metrics-author must pick a real guardrail (measures harm, fires on both arms) |
| 3 | Right metric, broken implementation | Prefer Observability event metrics; validate trace queries against real spans |
| 4 | Review checks diff, not data flow | Runtime data-flow check + extend judge rubric to "would this detect the harm?" |
| 5 | Manifest hand-edited as raw JSON | Assisted, schema-validated authoring for the release plan — no hand-editing |
| 6 | "No data" looks like "no regression" | In-flight: zero samples in an arm → warn/hold, don't complete |
| 7 | Test node hangs on .NET → red run | Sandbox/timeout the shell; test-node failure non-blocking for produced artifacts |

**The one-line takeaway for a design partner:** the orchestration is real and it
works, but *guarded-release safety depends entirely on metric quality* — and the
current agent + review layer can produce a confident-looking release whose
guardrail is blind. Hardening metric selection, adding a data-flow check, and
replacing hand-edited manifest JSON with assisted authoring are the highest-value
next steps.
