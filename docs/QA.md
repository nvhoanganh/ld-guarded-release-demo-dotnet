# LaunchDarkly AutoFactory — Customer Q&A

A frequently-asked-questions guide for teams evaluating **AutoFactory**, the
autonomous "software factory" that turns a pull request into a feature-flagged,
metric-instrumented, guarded release.

This document collects the questions we hear most from engineering, security,
and platform teams, with straight answers and the guardrails already in place.

> **Status:** AutoFactory is a working **design-partner prototype**, not a GA
> product. Some answers below distinguish what exists today from what is
> roadmapped. Where a mitigation is a configuration you apply, it is called out.

---

## 1. Prerequisites

### Q: Do we need LaunchDarkly already set up in our repositories?

**Yes — this is a hard prerequisite.** AutoFactory assumes each target repo is
already LaunchDarkly-integrated:

- the LaunchDarkly SDK is installed and a client is initialised,
- flags are already evaluated somewhere in the code (the agents detect and match
  your existing evaluation pattern), and
- metric telemetry is flowing (SDK `Track`, or the Observability/OpenTelemetry
  plugin) so a guarded release has a signal to monitor.

AutoFactory does **not** bootstrap a cold repository — it will not install the
SDK, provision an SDK key, or add a metric pipeline for you. On a repo with zero
LaunchDarkly wiring, the flag is still created in LaunchDarkly, but the code
wiring and the guarded release have nothing to build on. Wire LaunchDarkly
(flags **and** metrics) first.

---

## 2. Security

### Q: The AI agents write code and push to our branches. What stops them doing something malicious or wrong?

AutoFactory is an **assistive proposer, not an autonomous deployer**:

- Agents run inside **your CI** with a scoped token.
- Every change lands as a **commit on the pull-request branch — never a direct
  merge to a protected branch.**
- Your **existing PR review and approval gate is unchanged.** A human reviews,
  edits release intent, and merges. Nothing reaches production without that gate.
- New flags are created **dark** (serving the `control`/existing behaviour), and
  the agents are required to keep the control path byte-for-byte behaviour-
  preserving, so merging changes nothing live.
- Agents have a **fixed, declared toolset** (read files, edit files, create
  flag, run tests, commit) — no arbitrary shell access.

### Q: What secrets does AutoFactory hold, and how big is the blast radius?

Four, and they should each be scoped to the minimum:

| Secret | What it can do | Recommended scoping |
|---|---|---|
| `LD_API_KEY` | Create flags and start/stop releases in the **app** project | Scope to the app/data-plane project only (factory and app projects are kept separate by design for blast-radius isolation) |
| `GITHUB_TOKEN` | Read repos; for Phase 1, write to PR branches | Use a **fine-grained GitHub App / token**, scoped to named repos, read-only for Beacon |
| `BEACON_WEBHOOK_SECRET` | Authenticate deploy notifications to Beacon | Strong, rotated, stored in a secrets manager |
| `ANTHROPIC_API_KEY` (or `CURSOR_API_KEY`) | Run the agents | Standard provider key hygiene |

The most powerful secret is the **`LD_API_KEY`**, not the GitHub token — it can
create flags and drive releases. Scope it to the app project.

### Q: You say Beacon "only reads our `.release-flags/` folder" — is that a real boundary?

That is a **code-level** promise, not a token-level one. GitHub's Contents
permission is **per-repository, not per-path** — a read token that can read the
manifest folder can technically read any file in that repo. If reading source at
all is unacceptable, the mitigation is to **keep the release manifests in a
dedicated repo** so the token's reach is limited to that repo, or to accept a
read-only, fine-grained token scoped to the specific repos.

### Q: Does our source code leave our environment?

Yes — agent execution sends diffs and relevant file contents to the configured
LLM provider (Anthropic by default, or Cursor). For regulated or data-residency-
sensitive customers this is the key question to resolve up front:

- the execution provider is **flag-selectable** (`auto-factory-ai-provider`),
- we can discuss an enterprise/self-hosted model path, zero-retention endpoints,
  and a Data Processing Agreement.

If code cannot leave your boundary under any terms, that must be designed for
before adoption.

### Q: Beacon is an internet-facing endpoint that starts production releases. Isn't that risky?

Beacon's job is to receive a deploy notification and start the corresponding
guarded release. Controls:

- Every request must present `BEACON_WEBHOOK_SECRET` (verified in constant time).
- It can be run on a **private network** with a webhook relay, behind **mTLS**,
  an **IP allowlist**, and rate limiting.
- A triggered release can do no more than the flag allows, and LaunchDarkly
  **automatically rolls back** if a guardrail metric regresses — so even a
  spurious trigger is bounded and self-correcting.

### Q: What about prompt injection from a malicious pull request?

The PR content the agents read is untrusted input to an LLM with tools, so this
is a legitimate concern. Mitigations: the fixed toolset (no arbitrary
execution), flags created dark, the mandatory behaviour-preserving control path,
the downstream reviewer agent, and — decisively — the **human approval gate**
before merge.

### Q: For audit and compliance, who is the actor when a flag is created or code is committed?

Actions are attributable:

- commits are made under a **distinct bot identity** and appear in git history,
- flags are **tagged** (`auto-factory`, `auto-generated`, and the ticket ID),
- LaunchDarkly's **audit log** records every flag and release action,
- per-agent LLM calls are emitted as **observability spans** for an evidence
  trail, and the quality **judges** score each agent's output against its diff.

---

## 3. Process & Ways of Working

### Q: We already manage flags by hand with our own conventions. Will this fight us?

AutoFactory is opinionated on one point: **new flags are string-multivariate**
(`control` / `v1` / `v2` …), even in a codebase full of boolean `isEnabled`
gates — because only multivariate flags can take iteration variations on later
PRs. It **matches your SDK call style**, not your flag shape, and will extend a
boolean-only flag seam with a string method rather than force your code through
the wrong helper. Offer it as **assistive**: it proposes; your team disposes.

### Q: Won't this create hundreds of stale flags?

Auto-created flags are marked **`temporary: true`** and tagged, so they surface
in LaunchDarkly's existing flag-cleanup and code-references tooling. That said,
**flag cleanup (Phase 3) is explicitly out of scope** for AutoFactory today —
removal runs through your normal LaunchDarkly hygiene. Plan for it.

### Q: Can we trust a bot to choose the rollout plan (stages, metrics, percentages)?

You don't have to. Every manifest carries a **human-editable `releaseIntent`
block** that a reviewer edits at the approval gate:

- `action`: `auto` (release on deploy) · `hold` (do not release yet) · `manual`
  (a human runs the release),
- `notBefore`, `prerequisites`, `segments`, and free-text notes.

The default is `auto`. **For medium/high-risk changes we recommend defaulting to
`hold`**, so nothing releases until a human explicitly arms it.

### Q: We run a monorepo — many services in one repository, deployed independently. Does this work?

**Not cleanly today — this is a known gap.** Beacon's service registry maps each
service to a **repository** (`repo:`), and it discovers release manifests from a
**single `.release-flags/` directory at the repo root** — it does not scope
discovery by subfolder. In a large monorepo with independently-deployed
services, that means:

- all services in the repo share **one** `.release-flags/` directory,
- a deploy of service A runs discovery over the **whole** directory and can pick
  up manifests intended for service B,
- services are differentiated only by `side`/`scope` and fullstack coordination —
  **not by path**, so a subfolder deploy cannot say "only my package's flags."

The prototype effectively assumes **one repository per deployable service**. If
you run a monorepo with independent per-subfolder deploys, this needs custom
work — a per-service manifest convention (e.g. `.release-flags/<service>/`) and a
discovery change, or one manifest directory per package. Neither exists in the
prototype. Raise your repository topology with us early; a single-service repo
(or a monorepo deployed as one unit) works today, independent sub-deploys do not.

---

## 4. Reliability & Operations

### Q: Is this production-grade?

It is a **prototype** and should be evaluated as one:

- **Beacon runs single-instance** with **file-backed state**
  (`beacon-state.json`). Multi-instance needs a KV/DB store — the interface seam
  exists, the implementation does not yet.
- **No retry queue:** a lost deploy notification can leave a release "waiting"
  until it is manually re-sent. Beacon logs every waiting outcome so it is
  visible, and a re-POST re-runs discovery.
- **Fullstack coordination is best-effort** and depends on services exposing a
  reachable status endpoint.

### Q: Is the AI quality gate blocking?

No. The **judges are a sampled, non-blocking** evaluation layer that scores each
agent's output for evidence and observability. The **reviewer agent** is the
automated gate, and your **human PR review** remains the real one.

### Q: Who runs and supports Beacon?

Beacon is a container **you host** in your own infrastructure (any host that
gives it an HTTPS URL). One Beacon instance serves **many repositories** via its
service registry — you do not run one per repo. Ownership, uptime, and the
support model are part of the adoption conversation.

---

## 5. Commercial

### Q: What does this cost to run, and can we control the noise?

Every qualifying PR spins up multiple LLM agents, so there is per-PR token spend
and PR activity. You can constrain scope: AutoFactory **skips** flag creation for
`config_change`, `dependency_update`, `infrastructure`, `test_only`, and
`documentation` PRs, and applies selective rules elsewhere — so it engages on the
changes that actually warrant a flag.

### Q: Does this lock us into LaunchDarkly?

AutoFactory is deliberately **LaunchDarkly-native** — flags, metrics, AI Configs,
Observability, and the release API. That is a strength if you are standardising
on LaunchDarkly and a consideration if you run a multi-vendor flag estate.

---

## Summary — how to think about AutoFactory

> AutoFactory is an **assistive proposer, not an autonomous deployer.** Agents
> run in **your CI**, open changes as **PR commits behind a dark flag**, and
> **your team still reviews, sets release intent, and merges.** LaunchDarkly runs
> the guarded release server-side and **auto-rolls-back on metric regression.**
> The factory and application LaunchDarkly projects are **isolated** for blast
> radius. Run it self-hosted, with **your keys** and **your review gate**.

**Three things to settle before a rollout:**

1. **Scope the credentials** — a fine-grained, read-only GitHub App for Beacon;
   the LaunchDarkly API key limited to the app project.
2. **Resolve data residency** — agree the LLM provider path (and DPA) that keeps
   your security team comfortable with code leaving the boundary.
3. **Default to `hold`** on medium/high-risk changes, so no release arms itself
   without a human.
