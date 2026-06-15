# LaunchDarkly Guarded Release Demo — .NET 10

A minimal ASP.NET Core Web API that demonstrates LaunchDarkly's **Guarded Release** capability:
a boolean flag gates a "new checkout flow" that injects latency + errors, a k6 load test drives
traffic, and LaunchDarkly automatically rolls the flag back when metrics regress.

Guarded Release watches an **automatic metric** —
`http.latency;route=/api/checkout` — which LaunchDarkly's Observability module
auto-generates from the OpenTelemetry trace data emitted by ASP.NET Core. No custom
event tracking required on the app side.

## Prerequisites

- **.NET SDK 10.0+** — <https://dotnet.microsoft.com/download>
- **k6** — `brew install k6` (macOS) or see <https://k6.io/docs/get-started/installation/>
- **OpenTelemetry Collector (contrib)** — install instructions below in §3.
  The *contrib* distribution is required for the New Relic exporter.
- **A LaunchDarkly account** with an SDK key (server-side) and the Observability feature enabled
- **A New Relic account** with an ingest license key

## 1. Configure LaunchDarkly

In your LaunchDarkly project, set up the following manually:

### Feature flag

| Setting | Value |
|---|---|
| Key | `new-checkout-flow` |
| Kind | Boolean |
| Default rule | Serve `false` |

### Automatic metric (Observability)

After you run the app for ~30s and it sends trace data, an event key
`http.latency;route=/api/checkout` will appear under **Telemetry → Observability metrics**
(source: OpenTelemetry). Create a second metric:

| Setting | Value |
|---|---|
| Define metric using | LaunchDarkly hosted |
| Event kind | Custom |
| Event key | `http.latency;route=/api/checkout` |
| Metric definition | Average per request, then Average, **lower is better** |
| Unit | `ms` |
| Units without events | Excluded |
| Metric name | `http-latency-checkout` |

### Guarded rollout

On the `new-checkout-flow` flag → Targeting → Default rule → Serve **Guarded rollout**:

- Target variation: **true** (the bad variation)
- Target by: **request** (matches the metric's per-request unit)
- Metrics to monitor: **`http-latency-checkout`** with ✓ Auto rollback
- Rollout stages (recommended for live demos):
  `5% → 10% → 25% → 50% → 100%`, **5 minutes each** (20 min total)

## 2. Configure the app

`appsettings.json` is git-ignored to keep your SDK key out of the repo. Copy the example
file and fill in your key:

```bash
cp appsettings.example.json appsettings.json
```

Then edit `appsettings.json`:

```json
{
  "LaunchDarkly": {
    "SdkKey": "sdk-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
  }
}
```

Alternative: skip the copy and supply the key via environment variable instead (useful in
CI / containers where you don't want secrets on disk):

```bash
export LaunchDarkly__SdkKey="sdk-xxxx..."
```

## 3. Run the OTel Collector sidecar

The app no longer talks to LaunchDarkly directly. Instead it sends OTLP to a
local collector that fans out to both **LaunchDarkly** and **New Relic**. See
[`otel-collector-config.yaml`](otel-collector-config.yaml) for the pipeline.

### Install the collector

There is no Homebrew formula for `otelcol-contrib`. Grab the prebuilt binary
from the official [GitHub releases](https://github.com/open-telemetry/opentelemetry-collector-releases/releases).

macOS (Apple Silicon):

```bash
VERSION=0.154.0
curl -fsSLO "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v${VERSION}/otelcol-contrib_${VERSION}_darwin_arm64.tar.gz"
tar -xzf "otelcol-contrib_${VERSION}_darwin_arm64.tar.gz" otelcol-contrib
sudo mv otelcol-contrib /usr/local/bin/
otelcol-contrib --version   # verify
```

For Intel Macs, swap `darwin_arm64` → `darwin_amd64`. For Linux, the same
release page has `linux_amd64`, `linux_arm64`, `.deb`, and `.rpm` artifacts.
Pick the **`otelcol-contrib`** distribution (not `otelcol` or `otelcol-k8s`)
— the New Relic exporter is only bundled in contrib.

### Run it

In a dedicated terminal:

```bash
# bash / zsh
export LD_SDK_KEY="sdk-xxxx..."         # same key used by the app
export NEW_RELIC_LICENSE_KEY="..."      # NR ingest license key
otelcol-contrib --config=otel-collector-config.yaml
```

```fish
# fish
set -x LD_SDK_KEY "sdk-xxxx..."
set -x NEW_RELIC_LICENSE_KEY "..."
otelcol-contrib --config=otel-collector-config.yaml
```

The collector listens on `localhost:4318` (HTTP) and `:4317` (gRPC). Leave it
running while the app and k6 are active.

> **Why a sidecar?** It centralises destination config (the SDK key and NR
> key live in the collector, not the app), lets you fan out to multiple
> backends, and adds a place to do batching, sampling, and redaction without
> rebuilding the app. For a single-backend dev loop, you can skip the sidecar
> and use the `otel-integration` branch which exports directly to LaunchDarkly.

## 4. Run the app

```bash
dotnet restore
dotnet run
```

The app starts on `http://localhost:5000`. Smoke test:

```bash
curl -X POST http://localhost:5000/api/checkout \
  -H 'Content-Type: application/json' \
  -d '{"userId":"u-1","cartTotal":99.99}'
```

- With the flag serving **false** you'll see `{"engine":"v1", ...}` and latency 50–100 ms
- With the flag serving **true** you'll see `{"engine":"v2", ...}` at 300–800 ms,
  with ~20% returning HTTP 500

## 5. Drive load with k6

In a second terminal:

```bash
k6 run k6/load-test.js
```

The script ramps to 50 VUs over ~90s, sustains for 3 minutes, then cools down.
Each virtual user picks a random `userId` from `u-1` to `u-500` so LaunchDarkly's
rollout percentage distributes naturally.

While k6 runs:

1. In the LaunchDarkly UI, click **Start rollout** on the Guarded rollout
2. Watch the monitoring panel on the flag — `checkout-latency` (and `http-latency-checkout`)
   will show the `true` variation's average climbing far above the `false` baseline
3. Once LaunchDarkly accumulates enough samples and detects the regression, the rollout
   **automatically rolls back** and the flag returns to serving `false`

## How it works

- `Program.cs` wires up the **.NET OpenTelemetry SDK directly** — no LaunchDarkly
  Observability plugin. Traces, metrics, and logs are all exported via OTLP/HTTP to
  `http://localhost:4318` (the sidecar). The app is backend-agnostic.
- The sidecar collector (`otel-collector-config.yaml`) adds the
  `launchdarkly.project_id` resource attribute (sourced from `LD_SDK_KEY`) and fans
  out every signal to **two exporters**: LaunchDarkly's hosted OTLP collector and
  New Relic's OTLP endpoint. Same data, two destinations.
- `POST /api/checkout` evaluates `new-checkout-flow` with a multi-`Context` (a `user`
  kind keyed off the request's `userId`, plus a `request` kind keyed off the ASP.NET Core
  `TraceIdentifier`). It simulates fast/stable behaviour when `false` and slow/flaky
  behaviour when `true`.
- The `LdClient` has a `TracingHook` attached (from `LaunchDarkly.ServerSdk.Telemetry`)
  which emits a `feature_flag` span event with `feature_flag.context.key` on every
  variation call. **LaunchDarkly drops traces that don't include such an event**, so
  this hook is required whenever you wire OTel yourself instead of using the
  Observability plugin.
- ASP.NET Core auto-instrumentation creates a server span for every `/api/checkout`
  request. LaunchDarkly ingests those spans and surfaces a synthetic
  `http.latency;route=/api/checkout` event you can build a metric on top of — that's
  the metric driving the Guarded Release.

## Project layout

```
.
├── GuardedReleaseDemo.csproj      # .NET 10 web project, refs LD ServerSdk + ServerSdk.Telemetry + OpenTelemetry packages
├── Program.cs                     # Minimal API: OTel SDK wiring + LD TracingHook + POST /api/checkout
├── otel-collector-config.yaml     # Sidecar collector: receives OTLP, fans out to LaunchDarkly + New Relic
├── appsettings.example.json       # Template — copy to appsettings.json and add your SDK key
├── appsettings.json               # Local config (git-ignored; holds your real SDK key)
├── k6/
│   └── load-test.js               # k6 script: ramp + sustain + per-engine metrics split
└── README.md
```

## Cleanup

Stop the app with Ctrl+C. The lifetime hook flushes pending LD events and disposes the
client cleanly before exit.
