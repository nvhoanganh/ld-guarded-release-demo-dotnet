# AutoFactory — Minimum Setup & Gotchas

A practical, minimum-viable setup guide for standing up **AutoFactory** against a
single application repository. This repo — `ld-guarded-release-demo-dotnet`, the
.NET guarded-release demo — is used as the worked example of the *application*
being onboarded.

> **Status:** design-partner prototype. This is the smallest path to a working
> Phase 1 (agents create the flag + wire the PR) and Phase 2 (Beacon releases on
> deploy). Production hardening is called out under **Gotchas**.

---

## 0. Mental model (what you are wiring)

```
 PR opened ──▶ Phase 1 (agents, in CI) ──▶ commit on PR branch:
                 • create flag in LD (dark)      - code wrapped behind flag
                 • write .release-flags/pr-N.json (manifest)
 human reviews + merges
        │
 app deploys ──▶ deploy webhook ──▶ Beacon (your container) ──▶ LD release API
                                        └─ monitors guarded release to done/revert
```

You are responsible for hosting **one thing**: **Beacon**. Everything else is CI,
your existing app deploy, and LaunchDarkly (SaaS).

---

## 1. Prerequisites (have these first)

- **LaunchDarkly account with TWO projects:**
  - a **factory** project — holds the agent AI Configs + graph (Phase 1 reads it),
  - an **app** project — where flags/metrics are created and released (e.g.
    `default` / the AVTA project this demo already uses).
  Keeping them separate is the blast-radius boundary — do not collapse them.
- **LaunchDarkly SDK already wired in the app repo.** AutoFactory matches your
  existing evaluation pattern; it does **not** bootstrap a cold repo. This demo
  already evaluates `new-checkout-flow` via the server SDK — that is the
  precondition. Metrics must also be flowing (this repo's branches show the two
  ingestion styles) or the guarded release has nothing to monitor.
- **Node 20+** for local tooling / bootstrap (the GitHub Action runs on Node 24;
  the Cursor provider needs Node ≥ 22.13).
- **Docker** — to build and run Beacon.
- **An LLM provider key** — `ANTHROPIC_API_KEY` (default) or `CURSOR_API_KEY`.
- **A GitHub Personal Access Token / App** — see below.

---

## 2. Credentials — the minimum set

| Credential | Used by | Minimum scope |
|---|---|---|
| **GitHub PAT / App token** (`GITHUB_TOKEN`) | Beacon (read manifests), Phase 1 Action (write PR) | Beacon: **read-only Contents** on the app repo. Phase 1: **read/write** on the repo (commit to PR branch). Prefer a **fine-grained PAT** or a **GitHub App**, scoped to only the repos you onboard. |
| **LD API key** (`LD_API_KEY`, `api-...`) | Phase 1 (create flag/metric), Beacon (start release) | Scope to the **app** project. This is the most powerful secret — it can create flags and drive releases. |
| **LD server SDK key** (`LD_SDK_KEY`, `sdk-...`) | Phase 1 (read AI Configs + graph) | The **factory** project's environment. |
| **Beacon webhook secret** (`BEACON_WEBHOOK_SECRET`) | Beacon (authenticate deploy notifications) | Any strong random string; store in a secrets manager. |
| **LLM key** (`ANTHROPIC_API_KEY` / `CURSOR_API_KEY`) | Phase 1 agents | Standard provider key hygiene. |

### GitHub PAT — the exact shape

- **Fine-grained PAT** (recommended): Resource owner = your org; Repository
  access = **Only select repositories** → this repo; Permissions →
  **Contents: Read-only** for Beacon. (Phase 1's committing token needs
  **Contents: Read and write** + **Pull requests: Read and write**.)
- **Classic PAT** (works, blunt): `repo` scope — but this grants read/write to
  *all* your repos. Avoid for Beacon; if you must, isolate Beacon on a machine
  account.

---

## 3. Phase 1 — get the agents running (GitHub Action path)

1. Clone the AutoFactory repo, `npm install`.
2. `cp .env.example .env` and fill in: `LD_SDK_KEY`, `LD_API_KEY`,
   `LD_PROJECT_KEY` (factory), `LD_APP_PROJECT_KEY` (app), `ANTHROPIC_API_KEY`.
3. `npm run bootstrap` — provisions the six agent AI Configs, the two judges, the
   agent graph, and the operational flags into your **factory** project.
4. Drop the workflow template from `bootstrap/github-action-template/` into this
   app repo's `.github/workflows/`, and add the secrets to the repo/Environment.
5. Open a PR. The agents create a **dark** flag in the app project, wrap the
   change, and commit `.release-flags/pr-<N>.json` to the PR branch.

> Other front ends exist (Cursor extension, native Cursor automation, headless
> `autofactory` CLI / Claude Code skill) — same agents, same manifest, different
> trigger. The Action is the primary, verified path.

---

## 4. Phase 2 — run Beacon (Docker)

### 4.1 Register this repo as a service

Add an entry to `config/services.yaml` in the AutoFactory repo. Example for this
demo (single service, backend):

```yaml
services:
  guarded-release-demo:
    side: backend
    repo: nvhoanganh/ld-guarded-release-demo-dotnet
    statusUrl: https://<your-demo-host>/api/status   # returns the deployed SHA
    statusShaField: version
```

> The service **key** must match the `service` name your deploy notification
> sends. For the Railway adapter, that is the Railway service name verbatim.

### 4.2 Build and run

```bash
# from the AutoFactory repo root (Beacon depends on @auto-factory/shared)
docker build -f packages/beacon/Dockerfile -t auto-factory-beacon .

docker run -p 8080:8080 --env-file beacon.env auto-factory-beacon
```

`beacon.env` (minimum):

```dotenv
BEACON_WEBHOOK_SECRET=<strong-random-string>
GITHUB_TOKEN=<fine-grained read-only PAT>
LD_API_KEY=<api-... scoped to the app project>
LD_PROJECT_KEY=<app project key>
LD_ENVIRONMENT_KEY=production
# optional:
# PORT=8080
# BEACON_STATE_FILE=/data/beacon-state.json
# BEACON_MONITOR=true
# BEACON_MONITOR_POLL_MS=10000
```

Health check: `curl https://<beacon-host>/health` → `{ "ok": true }`.

### 4.3 Point deploy notifications at Beacon

After every deploy, something must POST to Beacon. Three options:

```bash
# Generic contract (CI step, script, or a human):
curl -X POST https://<beacon-host>/flag-releases \
  -H "x-beacon-secret: $BEACON_WEBHOOK_SECRET" \
  -H "content-type: application/json" \
  -d '{"service":"guarded-release-demo","sha":"<deployed-sha>"}'
```

- **Railway:** set the deploy webhook to
  `https://<beacon-host>/webhooks/railway?secret=<secret>` (Railway webhooks
  can't set custom headers, hence the query-string secret). Only `SUCCESS` events
  act.
- **Bundled notifier:** `auto-factory-notify` — a post-deploy hook that POSTs the
  deployed SHA for you.

---

## 5. End-to-end smoke test

1. Open a PR that changes business logic → confirm the agents create a dark flag
   and commit the manifest.
2. Edit the manifest's `releaseIntent` (e.g. keep `action: auto`), review, merge.
3. Deploy, then POST the deploy notification (step 4.3).
4. Watch Beacon logs: `[beacon] discovery: 1 new release flag(s) …` →
   `outcomes: <flag>=released`.
5. In LaunchDarkly, watch the guarded release progress — and confirm it
   auto-rolls-back if you force a metric regression.

---

## 6. Gotchas (learn these before they bite)

- **Build Beacon from the monorepo root**, not from `packages/beacon/` — it
  depends on the `@auto-factory/shared` workspace package. Building from the
  subfolder fails.
- **`PORT` defaults to 8080.** Map/expose it; set `PORT` if your host injects one.
- **State is file-backed and local.** `beacon-state.json` holds the last-seen SHA
  per service/env. On an ephemeral filesystem it resets on restart → Beacon loses
  its "previous SHA" and re-diffs from empty (treats all current manifests as
  new). **Mount a volume** at `BEACON_STATE_FILE`.
- **No retry queue.** A lost deploy notification leaves a release "waiting" until
  you re-POST the same `{service, sha}`. Beacon logs `[beacon] WAITING: …` so it
  is visible — watch for it.
- **`previousSha`:** explicit in the payload wins; otherwise Beacon uses its state
  store. First-ever deploy has no previous SHA → **every** current
  `.release-flags/*.json` is treated as new. Seed carefully on first run.
- **GitHub token reach is per-repo, not per-path.** "Beacon only reads
  `.release-flags/`" is a code promise; the token can read the whole repo. If
  that's unacceptable, keep manifests in a dedicated repo.
- **Fullstack scope needs both services deployed** and their `statusUrl`
  reachable from Beacon. Private-network-only status endpoints won't be reachable
  — the cross-check skips unreachable counterparts.
- **LaunchDarkly must be wired first (flags *and* metrics).** A guarded release
  with no metric source has nothing to monitor. This repo's four branches are the
  reference for the two metric-ingestion styles.
- **Flag shape is string-multivariate `control`/`v1`**, always — even if your repo
  is all boolean `isEnabled` gates. Don't be surprised by it; it's deliberate (so
  later PRs can add `v2`).
- **Two LD projects, two keys.** `LD_SDK_KEY` = factory (read AI Configs);
  `LD_API_KEY` = app (create/release). Mixing them up is the most common
  first-run error.
- **Default `releaseIntent.action` is `auto`.** For risky changes, set it to
  `hold` at the approval gate or nothing stops the release firing on deploy.
- **Webhook secret is mandatory on every POST** — 401 without it. Railway path
  must use `?secret=` (no custom headers).

---

## 7. Minimum checklist

- [ ] Two LD projects (factory + app); SDK already wired in the app repo (+ metrics).
- [ ] Fine-grained GitHub PAT/App: read-only for Beacon; read/write for Phase 1.
- [ ] `LD_API_KEY` scoped to the app project; `LD_SDK_KEY` for the factory project.
- [ ] `npm run bootstrap` ran → agents + graph provisioned in the factory project.
- [ ] Phase 1 workflow added to the app repo + secrets set.
- [ ] `config/services.yaml` entry for the repo (key = deploy `service` name).
- [ ] Beacon built from monorepo root, running, `/health` green, state volume mounted.
- [ ] Deploy notifications POST to Beacon (generic, Railway, or notifier) with the secret.
- [ ] Smoke test passed: PR → dark flag + manifest → merge → deploy → release.
