import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Rate } from 'k6/metrics';

// Custom k6 metrics so you can watch the engine split in real time.
const engineV1Latency = new Trend('engine_v1_latency', true);
const engineV2Latency = new Trend('engine_v2_latency', true);
const engineV2Errors = new Rate('engine_v2_errors');
const engineV1Errors = new Rate('engine_v1_errors');

export const options = {
  stages: [
    { duration: '30s', target: 10 },   // warm up — baseline (flag off / 0% rollout)
    { duration: '60s', target: 50 },   // ramp — start your Guarded Release in LD now
    { duration: '180s', target: 50 },  // sustain — watch LD auto-rollback fire
    { duration: '30s', target: 0 },    // cool down
  ],
  thresholds: {
    // Informational — these will trip when the new engine is serving traffic,
    // which is the whole point of the demo.
    http_req_failed: ['rate<0.05'],
    http_req_duration: ['p(95)<200'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

export default function () {
  // Spread across 500 distinct user contexts so LD's rollout % distributes
  // requests evenly across the variation.
  const userId = `u-${Math.floor(Math.random() * 500) + 1}`;
  const cartTotal = Math.round(Math.random() * 50000) / 100; // 0.00 - 500.00

  const payload = JSON.stringify({ userId, cartTotal });
  const params = {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'POST /api/checkout' },
  };

  const res = http.post(`${BASE_URL}/api/checkout`, payload, params);

  const ok = check(res, {
    'status is 200': (r) => r.status === 200,
  });

  // Split metrics by engine variation reported in the response body so you can
  // see the new vs old performance side-by-side in the k6 summary.
  let engine = 'unknown';
  try {
    const body = res.json();
    engine = body && body.engine ? body.engine : 'unknown';
  } catch (_) {
    // 500s come back as JSON too but be defensive.
  }

  if (engine === 'v1') {
    engineV1Latency.add(res.timings.duration);
    engineV1Errors.add(!ok);
  } else if (engine === 'v2') {
    engineV2Latency.add(res.timings.duration);
    engineV2Errors.add(!ok);
  }

  sleep(Math.random() * 0.5); // 0-500ms think time
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data),
  };
}

// Minimal text summary so the script is self-contained (no external imports).
function textSummary(data) {
  const lines = [];
  lines.push('');
  lines.push('=== Guarded Release demo k6 summary ===');
  const m = data.metrics;
  const fmt = (name) => {
    const x = m[name];
    if (!x || !x.values) return `${name}: (no data)`;
    const v = x.values;
    if ('avg' in v) {
      return `${name}: avg=${v.avg.toFixed(1)}ms  p95=${(v['p(95)'] || 0).toFixed(1)}ms  p99=${(v['p(99)'] || 0).toFixed(1)}ms`;
    }
    if ('rate' in v) {
      return `${name}: ${(v.rate * 100).toFixed(2)}%`;
    }
    return `${name}: ${JSON.stringify(v)}`;
  };
  lines.push(fmt('http_req_duration'));
  lines.push(fmt('http_req_failed'));
  lines.push(fmt('engine_v1_latency'));
  lines.push(fmt('engine_v2_latency'));
  lines.push(fmt('engine_v1_errors'));
  lines.push(fmt('engine_v2_errors'));
  lines.push('');
  return lines.join('\n');
}
