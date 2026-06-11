# LaunchDarkly Guarded Release Demo — .NET 10

A minimal ASP.NET Core Web API that demonstrates LaunchDarkly's **Guarded Release** capability:
a boolean flag gates a "new checkout flow" that injects latency + errors, a k6 load test drives
traffic, and LaunchDarkly automatically rolls the flag back when metrics regress.

**This branch (`no-otel-use-sdk-metric`) shows the pure-SDK variant** — no
`LaunchDarkly.Observability` package, no OpenTelemetry, no auto-generated metrics. The
Guarded Release is driven by a custom numeric metric (`checkout-latency`) emitted via the
standard `LdClient.Track()` API. Use this branch if you don't want LaunchDarkly's
Observability module in your dependency tree.

> See the `main` branch for the OpenTelemetry/Observability-driven variant where the
> metric is auto-generated from trace data and no `Track()` calls are needed.

## Prerequisites

- **.NET SDK 10.0+** — <https://dotnet.microsoft.com/download>
- **k6** — `brew install k6` (macOS) or see <https://k6.io/docs/get-started/installation/>
- **A LaunchDarkly account** with a server-side SDK key. The Observability feature is
  **not** required for this branch.

## 1. Configure LaunchDarkly

In your LaunchDarkly project, set up the following manually:

### Feature flag

| Setting | Value |
|---|---|
| Key | `new-checkout-flow` |
| Kind | Boolean |
| Default rule | Serve `false` |

### Custom metric

| Setting | Value |
|---|---|
| Define metric using | LaunchDarkly hosted |
| Event kind | Custom |
| Event key | `checkout-latency` |
| Metric definition | Average per request, then Average, **lower is better** |
| Unit | `ms` |
| Units without events | Excluded |
| Metric name | `checkout-latency` |

This metric is fed by `LdClient.Track("checkout-latency", context, null, elapsedMs)`
inside the `/api/checkout` handler.

### Guarded rollout

On the `new-checkout-flow` flag → Targeting → Default rule → Serve **Guarded rollout**:

- Target variation: **true** (the bad variation)
- Target by: **request** (matches the metric's per-request unit)
- Metrics to monitor: **`checkout-latency`** with ✓ Auto rollback
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

## 3. Run

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

## 4. Drive load with k6

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

- `Program.cs` constructs a plain `LdClient` from `LaunchDarkly.ServerSdk` — no plugins,
  no OpenTelemetry instrumentation, no extra packages.
- `POST /api/checkout` evaluates `new-checkout-flow` with a multi-`Context` (a `user`
  kind keyed off the request's `userId`, plus a `request` kind keyed off the ASP.NET Core
  `TraceIdentifier`). It simulates fast/stable behaviour when `false` and slow/flaky
  behaviour when `true`.
- After each request the handler measures elapsed time and calls
  `ld.Track("checkout-latency", context, LdValue.Null, sw.ElapsedMilliseconds)`. That
  Track event is what LaunchDarkly's Guarded Release consumes to detect regression.

## Project layout

```
.
├── GuardedReleaseDemo.csproj   # .NET 10 web project, refs LaunchDarkly.ServerSdk only
├── Program.cs                  # Minimal API: LdClient + POST /api/checkout with Track()
├── appsettings.example.json    # Template — copy to appsettings.json and add your SDK key
├── appsettings.json            # Local config (git-ignored; holds your real SDK key)
├── k6/
│   └── load-test.js            # k6 script: ramp + sustain + per-engine metrics split
└── README.md
```

## Cleanup

Stop the app with Ctrl+C. The lifetime hook flushes pending LD events and disposes the
client cleanly before exit.
