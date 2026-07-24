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

## Finding 8 — The randomization unit is silently defaulted, but it's a real design decision

When the metrics-author runs, it reads the rollout's randomization unit from the
manifest and, if it's unset, applies rule **M03**: *"else default `user` and note
the assumption."* In our second run the manifest had no unit set, so it fell
through to **`user`** — with only a one-line "assumption noted" that no human
reads before the release ships.

The unit is **not** a safe default. It depends on factors an agent can't infer
from a diff — and often a human can't either without context:

| Factor | Pulls toward `user` | Pulls toward `request` |
|---|---|---|
| Stickiness — must one user always see the same variation? | Yes (UX consistency) | No (stateless call) |
| What the harm/metric measures | user-level (conversion, revenue) | request-level (latency, error rate) |
| Statistical power | fewer units, slower detection | more units, faster detection |
| Independence | requests from one user are correlated | fine when the effect is per-request |

For our backend latency change on a stateless endpoint, **`request` was the
better unit** (per-request harm, no stickiness need, more samples). The app even
already models a `request` context. But the agent defaulted to `user`.

Compounding this: the app builds a **multi-context** (`user` + `request`), where
the `request` kind is keyed by an **ephemeral per-call `TraceIdentifier`**. Nothing
*links* the request to the user — they're two independent context kinds that merely
co-occur. So the multi-context doesn't resolve the unit choice for the agent; a
human still has to decide which kind the release randomizes on, and whether
stickiness matters.

**Remediation (design, not a one-line default flip):** the unit should be an
**owned** decision, not a silent fallback. Best options, combined:
1. **Human-owned in `releaseIntent`** — the agent *proposes* a unit **with its
   reasoning**, the approver confirms/overrides at the gate.
2. **Derive it from the primary guardrail's level** — per-request metric → `request`;
   per-user metric → `user`.
3. **Never silently default** — if unset and the agent can't justify a choice with
   high confidence, **HOLD** for a human. Flipping the default to `request` would be
   the same mistake in the other direction.

---

## Finding 9 — "Reuse existing metric" loses to "unit must match", so the agent re-authors

After the Finding 2/3 fix, the retrained metrics-author *did* try to reuse an
existing metric — but for latency it still **created a new `track()` metric**
instead of reusing `http-latency-checkout`, even though the research-planner's
brief explicitly said *"the Metrics Author should reuse it."*

Cause: a **rule conflict**. The agent chose `randomizationUnit: user` (Finding 8's
default), and `http-latency-checkout` was **`request`-scoped**. Its mandatory
unit-match rule (M03) then **rejected** the reuse (request ≠ user) → so it fell
back to authoring a new user-scoped metric.

```
reuse http-latency-checkout   (planner + guidance)     ← wanted
        ✗ blocked by
randomizationUnit = user  +  http-latency-checkout = request  +  M03
        ↓
new user-scoped metric created instead
```

**Two remediations (both work; combine them):**
- **Agent side:** when a suitable existing shared-path metric exists, **adopt its
  randomization unit** for the rollout rather than defaulting the unit first and
  then rejecting the metric.
- **Metric side:** shape shared observability metrics as **dual-unit**. Because the
  `http.latency;route=/api/checkout` event carries **both** `user` and `request`
  keys, `http-latency-checkout` can be defined with **both** analysis units — making
  it reusable by *any* rollout unit. (We verified this in the LD UI: enabling both
  units keeps the metric "Healthy".) A dual-unit shared metric removes the conflict
  entirely — the agent just reuses it.

---

## Finding 10 — Flag cleanup (Phase 3) is out of scope: code *and* flag debt accumulates

AutoFactory covers Phase 1 (create + wrap) and Phase 2 (release), but **Phase 3
(flag cleanup) is explicitly out of scope** — "existing LaunchDarkly
functionality." Nothing in the pipeline ever removes a flag or its code.

Every flagged PR leaves scaffolding behind **permanently** until a human removes
it: a flag evaluation (`ld.StringVariation(...)`), an `if (variation == "v1") { … }`
branch plus the implicit `control` path, the `ld.Track(...)` calls, and the flag
itself in LaunchDarkly. Two PRs into this demo, the checkout handler already stacks
**two** flag blocks (`enable-fraud-screening`, `enable-checkout-recommendations`),
both currently dead (both releases rolled back) but still present. N PRs → N blocks.

There is **no trigger, no agent, no sweep.** The factory helps a *human-driven*
cleanup by marking flags `temporary: true`, tagging them `auto-factory` /
`auto-generated`, and recording an `ld-find-code-refs` artifact — breadcrumbs, not
removal. Cleanup happens only when a person (or your existing LD lifecycle process)
inlines the winner / deletes the loser's branch, removes the eval, and archives the
flag (guided by Code references).

**Remediation:** a real Phase 3 — the mirror of Phase 1. Once a release reaches a
terminal state, an agent opens a **cleanup PR**: inline the winning variation or
delete the reverted one's branch, remove the flag evaluation and now-dead helper,
and archive the flag, all guided by code-refs. This is the single biggest missing
piece for long-term use.

---

## Finding 11 — AutoFactory + an AI cleanup agent (Vega): the loop was feared, but the classifier held (observed live)

We ran this end-to-end. LaunchDarkly **Vega** cleaned up the reverted
`enable-checkout-recommendations` flag: it removed the flag evaluation and its
`v1` branch and opened **PR #3** (`vega/flag-cleanup-…` → `software-factory`,
labelled `vega-pr`). Because AutoFactory Phase 1 triggers on `pull_request`, that
cleanup PR **did fire Phase 1** — the loop's first step happened for real.

**But it did NOT re-flag.** The research-planner classified it correctly and
short-circuited the whole chain:

```
research-planner: flag_worthy=false, flag_action=none,
                  risk_score=0.15, skip_flagging=true
Skipped: manifest-steward, flag-implementer, metrics-author,
         flag-testing, code-reviewer
Verdict: "no flag needed — nothing to review"
```

So the feared infinite create/remove loop **did not materialize** — AutoFactory's
own classifier recognized "this removes a flag" and declined to re-flag. Honest
correction to our first hypothesis: *the factory was smart enough on its own.*

**What actually costs something:**
1. **A wasted Phase 1 run** — the research-planner *ran* (compute + a PR comment)
   before deciding to skip.
2. **Reliance on the classifier being right on every cleanup diff.** This one was a
   simple removal (risk 0.15). A cleanup that inlines a more complex path could look
   like business logic and be misjudged — the skip was *incidental to a low risk
   score*, not an explicit "this is a cleanup" decision.

### Where the fix belongs — in the agent, not the CI YAML

A first instinct was a workflow guard
(`if: … && !startsWith(github.head_ref, 'vega/') && !contains(labels, 'vega-pr')`).
**That is the wrong layer** and we reverted it. AutoFactory's whole premise is that
decisions live in the **factory's control plane** (the LaunchDarkly AI configs), not
hardcoded per-repo in workflow YAML. A YAML guard must be copied into every
onboarded repo, hardcodes "vega", is invisible to the agents, and produces no
explicit "skipped because cleanup" record.

**Applied fix — an explicit cleanup gate in the research-planner** (a config-as-code
edit to `autofactory-research-planner`, synced to LD via the bridge). Before risk
scoring, the agent detects a cleanup PR from **`{{PR_TITLE}}`** (`[Vega]` / "remove"),
**`{{PR_BRANCH}}`** (`vega/`, `*flag-cleanup*`), or a **diff that removes a flag
evaluation**, and skips **explicitly**: `flag_worthy=false`, `flag_action=none`,
`skip_flagging=true`, with a reason that *names* the cleanup. This is stronger than
the incidental low-risk skip — it's a deliberate, auditable decision, editable in
LaunchDarkly, that every repo's agent inherits with zero per-repo config.

**Takeaway:** the loop is real as a *risk* but the factory's classifier already
breaks it; the right hardening is to make that skip **explicit and cleanup-aware in
the agent**, not to bolt a guard onto CI.

---

## Finding 12 — Cleanup conflates a *reverted* release with an *abandoned* feature (and pushed an unverified build)

When Vega cleaned up `enable-checkout-recommendations`, it reasoned:
> *"the release **rolled back**, so `control` is the winning/stable variation…
> Cleanup = remove the flag eval and the dead `v1` branch, keep control."*

So it **deleted the entire recommendations feature** (`EnrichCheckout`, the `v1`
branch, the response field). Internally consistent — but semantically wrong: **a
guarded release rolling back means "v1 regressed *this time*," which almost always
means "fix it and re-release," not "abandon the feature."** A reverted flag and an
abandoned flag look **identical** in state (serving `control`, no rollout), so the
cleanup agent cannot tell them apart:

| Flag state after revert | Two very different intents |
|---|---|
| serving `control`, no rollout | **abandon** v1 → delete the code (what Vega did) |
| serving `control`, no rollout | **retry**: optimize v1, re-release (the likely real intent) |

Treating "serving control" as "control won, delete v1" will **silently delete
features teams meant to iterate on.**

Compounding it: Vega hit a **build failure** (`NETSDK1045` — the project targets
.NET 10, its environment only had the .NET 9 SDK), **dismissed it** as an
environment limitation, and **committed + pushed anyway** — so it opened a cleanup
PR containing code it **could not compile or verify**.

**Remediation:**
- **Only auto-clean on a *completed* release** (v1 rolled to 100%), where "delete
  the loser" is unambiguous. On a **reverted** release, **hold for a human** — the
  intent (abandon vs retry) is not encoded in the flag state.
- Carry the intent explicitly: a flag tag or a `releaseIntent`-style field
  (`abandon` / `retry-pending`) the cleanup agent must respect.
- **Never push a cleanup that fails to build.** A build failure the agent can't
  resolve should block the PR, not be dismissed as "environmental."

---

## Finding 13 — The agent's improvement isn't sticky: metric quality is non-deterministic run-to-run

After the metrics-author was taught to reuse existing metrics and reason about the
randomization unit (Findings 2/3/8/9), it did exactly that on one PR — and then
**regressed on the very next one, with the same config.**

| | PR #4 (richer recs) | PR #5 (tiny latency bump — same config) |
|---|---|---|
| Latency guardrail | **reused `http-latency-checkout`** ✓ | **authored a new `…-latency` metric** ✗ |
| Metric has data? | yes (OTel event, both arms) | **no** — event metric the code never emits → **blind** |
| Randomization unit | **`request`, with reasoning** ✓ | **defaulted to `user`** ✗ |
| Manifest notes | accurate | **called it "trace-backed" when it's a custom event** ✗ |

Same agent instructions, same kind of change, **opposite (and wrong) output.** On
PR #5 a human had to catch the blind metric and swap the guardrail to
`http-latency-checkout` again — the exact fix from PR #1, re-applied.

**Why it matters:** a config edit that improves the agent *on average* does **not
guarantee** the behavior on any given PR. You cannot "fix the prompt once" and
trust it — the agent is a sampler, not a program. This is the deeper limitation
behind Findings 2/3/9: even after teaching, **every run needs verification**,
because the guardrail can silently be blind again.

**Remediation (belt-and-suspenders, since the prompt alone isn't reliable):**
- **A deterministic post-check** (not another agent): after the metrics-author
  runs, a code check that (a) every manifest `metricKey` exists, (b) each is
  receiving events / is a known-good shared metric, and (c) the randomization unit
  matches. Fail the run (or hold) if not. This is the data-flow check from Finding 4,
  made mandatory — the only reliable guard against a non-deterministic author.
- **Prefer a fixed policy over agent choice for the guardrail metric** where a
  canonical one exists (e.g. always attach the endpoint's `http.latency;route=…`
  metric for a latency-bearing change), leaving the agent to *add* diagnostics, not
  *pick* the gate.

---

## Finding 14 (headline) — No practical-significance threshold: auto-rollback fires on *any* real regression, however small

This is the most consequential finding for real-world use. LaunchDarkly's guarded
rollback is **statistical-significance-based with no effect-size tolerance** — you
cannot say *"roll back only if >X% worse."* Verified live, twice:

| Change (code) | measured v1 vs control | samples | Load | Outcome |
|---|---|---|---|---|
| enrichment `200-400` | **+26.6 ms** (594 vs 568) | — | 50 VUs | rolled back in ~62s |
| tiny bump `230-330` | **+17 ms** (579 vs 562), CI **1.9–32.1 ms** | **171** | **10 VUs** | rolled back in ~2m11s |

Both were **regression** reverts (not "insufficient sample size"). The tiny-bump
run is the decisive one: **only 171 request samples at a gentle 10 VUs**, a **+17ms
(~3%)** difference, and the **confidence interval's lower bound was 1.9 ms > 0** — so
LD was confident v1 is *at least ~2ms worse* and rolled back. Lowering load from
50→10 VUs did not save it; it just took 2 min instead of 60s.

**Why even +10ms is caught — the standard error:**

```
SEM = σ / √n
```

Per-request latency spread here is σ ≈ ~45ms (fraud + enrich variance). At 10 VUs
for ~2 min → ~470 v1 samples → SEM ≈ 45/√470 ≈ **~2ms**. So a **+10ms** mean
difference is **~5 standard errors** → overwhelmingly significant → rollback. As
sample size grows, **SEM → 0**, so **any genuine regression — +2ms, +1ms — becomes
significant given enough traffic.** Real production volume (millions of requests)
makes the SEM microscopic; a *reproducible* sub-1% regression will trip it.

**Consequence:** in real life **every release carries some small, acceptable latency
delta.** A significance-only gate rolls back on all of them once traffic is high
enough → guarded rollout **over-triggers and is unusable as-is** for normal
services. This is exactly the "this won't work in production" concern: there is no
knob to express *"I accept up to 2% slower."*

**Remediations:**
- **Ideal:** a configurable **practical-significance / minimum-effect threshold** on
  the guarded rollout (*"roll back only if worse by >X% with confidence"*). Not
  exposed in the release API this pipeline uses; an open ask for the LD
  guarded-rollout product team. (We could not confirm LD supports one anywhere — the
  instruction surface here only has `autoRollback: true/false`.)
- **Practical workaround today — bake tolerance into the *metric*:** monitor a
  metric that only registers requests **over an SLO** (e.g. count of requests
  >800ms), or a high percentile, so a small median shift that stays within SLO
  **doesn't register as a regression at all.** This moves "how much is acceptable"
  from the (absent) rollout threshold into the metric definition — the real-world way
  teams make guarded rollouts usable.

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
- **The improvement loop works.** After editing the metrics-author's instructions
  (config-as-code, no product-code change) to fix the blind-metric problem, the
  **next PR's agent authored a working guardrail on its own** — a `track()`-based
  latency metric on the shared path — and the guarded release **auto-rolled-back on
  the agent-authored metric** (v1 292 ms vs control 72 ms, +219 ms). Found a gap →
  taught the agent as config → verified on the next run. (That same run surfaced
  Finding 9 — the reuse-vs-unit-match conflict — which is the loop working: each
  pass reveals the next refinement.)

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
| 8 | Randomization unit silently defaulted to `user` | Make it an owned decision: agent proposes with reasoning → human confirms in `releaseIntent`; derive from the guardrail's level; never silently default |
| 9 | "Reuse" loses to "unit must match" → re-authors | Agent adopts the reused metric's unit; and/or shape shared metrics as **dual-unit** so any rollout can reuse them |
| 10 | Flag cleanup out of scope — code + flag debt piles up | A real Phase 3: on a terminal release, an agent opens a cleanup PR (inline winner / delete loser, remove flag) guided by code-refs |
| 11 | Cleanup PR triggers Phase 1 (loop feared) — but the classifier correctly skipped | Observed live: no re-flag. Make the skip **explicit + cleanup-aware in the research-planner** (title/branch/diff-shape), not a per-repo CI YAML guard — the decision belongs in the factory's control plane |
| 12 | Cleanup conflates a *reverted* release with an *abandoned* feature; pushed an unverified build | Auto-clean only on a *completed* release; **hold on revert**; carry abandon-vs-retry intent explicitly; never push a cleanup that fails to build |
| 13 | Agent improvement isn't sticky — metric quality is non-deterministic run-to-run | A **deterministic post-check** (every metricKey exists + receives data + unit matches; fail/hold otherwise); prefer a fixed policy for the guardrail metric, let the agent add diagnostics |
| 14 **(headline)** | No practical-significance threshold — auto-rollback fires on *any* real regression (verified +17ms/3% at 171 samples) | A configurable **effect-size tolerance** (`>X% worse`) — not exposed by the API here, an ask for the LD product team; **workaround: SLO-shaped metric** (count requests over a threshold) so small in-SLO shifts don't register |

**The one-line takeaway for a design partner:** the orchestration is real and it
works — and the **improvement loop is real too** (we fixed an agent as config and
verified it on the next PR). But *guarded-release safety depends entirely on metric
quality*, several consequential decisions are **silently defaulted** rather than
owned (metric choice, randomization unit), and the lifecycle is **half-closed** —
there is no cleanup, and pairing with an AI cleanup agent risks a create/remove
loop. Highest-value next steps: harden metric selection + add a data-flow check,
make the randomization-unit and manifest decisions human-owned/assisted, and build
Phase 3 cleanup with an explicit anti-loop contract.
