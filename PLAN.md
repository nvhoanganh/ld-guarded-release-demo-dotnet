# Plan: .NET 10 Guarded Release Demo with LaunchDarkly

## Context
Greenfield demo app showing LaunchDarkly Guarded Release: a boolean flag gates a "new checkout flow" that deliberately injects latency + errors. k6 hammers the endpoint, metrics regress, LD auto-rolls back. No existing codebase.

User-confirmed choices:
- Flag key: `new-checkout-flow` (boolean) — endpoint: `POST /api/checkout`
- SDK key supplied via `appsettings.json` placeholder
- Project folder: `/Users/anthonynguyen/sources/ld-guarded-release-demo-dotnet`

---

## Project Layout

```
/Users/anthonynguyen/sources/ld-guarded-release-demo-dotnet/
├── GuardedReleaseDemo.csproj
├── Program.cs
├── appsettings.json
├── PLAN.md            (this file)
└── k6/
    └── load-test.js
```

---

## NuGet Packages

| Package | Purpose |
|---|---|
| `LaunchDarkly.ServerSdk` (8.*) | Core flag evaluation + custom event tracking (`Track`) |
| `LaunchDarkly.Observability` (1.*) | LD's Observability module — wraps OTel, ships traces/metrics/logs to LD's collector. Provides `AddLaunchDarklyInstrumentation()` extension on `IServiceCollection` and `ILoggingBuilder`. Bundles AspNetCore + HttpClient + Runtime + Process instrumentation. |
| `LaunchDarkly.ServerSdk.Telemetry` (1.*) | `TracingHook` that auto-creates OTel spans for flag evaluations, correlating evaluations with metrics in LD |

We will **not** wire `AddOtlpExporter` manually — the Observability package handles the OTLP endpoint + auth.

---

## Flag & Metrics (user creates these in LD UI)

| Item | Key | Type | Notes |
|---|---|---|---|
| Flag | `new-checkout-flow` | Boolean | Default rule: false |
| Custom metric | `checkout-latency` | Numeric, lower is better, unit `ms` | Event key `checkout-latency`; tracked via `LdClient.Track()` |
| Automatic metric | `http.server.request.duration` (or error-rate equivalent) | From Observability module | Appears in LD's metric picker once OTel data flows |

---

## appsettings.json

```json
{
  "LaunchDarkly": {
    "SdkKey": "YOUR_SDK_KEY_HERE"
  },
  "Logging": {
    "LogLevel": { "Default": "Information" }
  },
  "AllowedHosts": "*"
}
```

The same SDK key drives both `LdClient` and `AddLaunchDarklyInstrumentation` (the Observability module uses it as the project credential).

---

## Endpoint: POST /api/checkout

Request body: `{ "userId": "u-123", "cartTotal": 99.99 }`

**Flag OFF (old flow):**
- Latency: 50–100 ms
- Error rate: 0%
- `200 { engine: "v1", orderId, processingMs }`

**Flag ON (new flow):**
- Latency: 300–800 ms
- Error rate: ~20% → `500 { error: "payment processor timeout" }`
- Else: `200 { engine: "v2", orderId, processingMs }`

**Both paths:**
1. Evaluate flag with `Context.Builder(userId).Kind("user").Build()`
2. `ldClient.Track("checkout-latency", context, null, elapsedMs)`
3. ASP.NET Core auto-instrumentation records `http.server.request.duration` + error status automatically

---

## Program.cs structure

```
1. Read SdkKey from configuration
2. Build LdClient as singleton, with TracingHook:
     var hook = TracingHook.Builder().Build();
     var ldConfig = Configuration.Builder(sdkKey)
         .Hooks(Components.Hooks().Add(hook))
         .Build();
     builder.Services.AddSingleton(new LdClient(ldConfig));
3. Wire LD Observability:
     builder.Services.AddLaunchDarklyInstrumentation(o => {
         o.ProjectId   = sdkKey;
         o.ServiceName = "guarded-release-demo";
     });
     builder.Logging.AddLaunchDarklyInstrumentation(o => {
         o.ProjectId   = sdkKey;
         o.ServiceName = "guarded-release-demo";
     });
4. Map POST /api/checkout (minimal API)
5. app.Run()
```

*Caveat:* if `LaunchDarkly.Observability` exports the extension under a different name (e.g. `AddObservability`), I'll adjust at implementation time — its public surface mirrors `Highlight.ASPCore` upstream.

---

## k6 Load Test (`k6/load-test.js`)

Stages:
```
0  → 10 VUs over 30s   (warm up — baseline, flag OFF / 0% rollout)
10 → 50 VUs over 60s   (ramp — bump rollout in LD here)
50 VUs for 180s        (sustain — watch LD auto-rollback fire)
50 → 0  VUs over 30s   (cool down)
```

Per iteration:
- Random `userId` from `u-1` … `u-500` so rollout % distributes naturally
- `POST /api/checkout` with random `cartTotal`
- Checks: `status === 200`
- Thresholds (informational): `http_req_duration{p(95)} < 200`, `http_req_failed < 0.05`

k6 prints real-time p50/p95/p99 + error rate in the terminal so you can watch regression while LD's Guarded Release fires.

---

## LD Setup Checklist (you do this manually)

1. **Create flag** `new-checkout-flow` — boolean, default off
2. **Create custom metric** `checkout-latency` — numeric, event key `checkout-latency`, lower is better, unit ms
3. **Wait for Observability metric** `http.server.request.duration` to surface in metric picker after first OTLP batch
4. **Configure Guarded Release** on `new-checkout-flow`:
   - Monitored metrics: `checkout-latency` + `http.server.request.duration`
   - Initial stage: e.g. 10% rollout
   - Auto-rollback when either metric regresses vs control
5. Drop SDK key into `appsettings.json`, start app, run k6, click **Start Release** in LD

---

## Verification

1. `cd /Users/anthonynguyen/sources/ld-guarded-release-demo-dotnet && dotnet restore && dotnet run`
2. App listens on `http://localhost:5000`
3. Smoke test:
   ```
   curl -X POST http://localhost:5000/api/checkout \
     -H 'Content-Type: application/json' \
     -d '{"userId":"u-1","cartTotal":99.99}'
   ```
   → `200 { engine: "v1", ... }`
4. In LD UI, flip flag to 100% true → re-curl → see `engine: "v2"` + occasional 500s
5. `k6 run k6/load-test.js`
6. Watch LD Guarded Release UI roll the flag back automatically as metrics regress; k6 p95 + error rate recover ~1–2 min after rollback fires
