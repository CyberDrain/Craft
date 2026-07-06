// k6 load test for CRAFT in http-only mode — drives the synthetic PerfApi endpoints and records
// per-endpoint latency, throughput and error rate. Isolates the PowerShell HTTP dispatch pipeline.
//
// Env:
//   BASE      base URL (default http://sut:8080)
//   VUS       virtual users for open-throttle mode (default 10)
//   RATE      iterations/sec for fixed-arrival-rate mode (default 0 = open throttle)
//   DURATION  test duration (default 30s)
//   ONLY      focus a single endpoint by name (e.g. PerfSleep); default = weighted mix
//   CPU_MS    ?ms for PerfCpu   (default 20)
//   SLEEP_MS  ?ms for PerfSleep (default 100)
//   JSON_N    ?n  for PerfJson  (default 1000)
//
// Output: --summary-export /out/<label>.k6.json (written by run-api.ps1)

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const BASE = __ENV.BASE || 'http://sut:8080';
const RATE = parseInt(__ENV.RATE || '0', 10);
const DURATION = __ENV.DURATION || '30s';
const VUS = parseInt(__ENV.VUS || '10', 10);
const ONLY = __ENV.ONLY || '';
const CPU_MS = __ENV.CPU_MS || '20';
const SLEEP_MS = __ENV.SLEEP_MS || '100';
const JSON_N = __ENV.JSON_N || '1000';

// Endpoint catalogue with mix weights (higher weight = requested more often in the default mix).
const endpoints = [
  { name: 'PerfPing', url: '/API/PerfPing', weight: 5 },
  { name: 'PerfEcho', url: '/API/PerfEcho?hello=world&n=2', weight: 3 },
  { name: 'PerfCpu', url: `/API/PerfCpu?ms=${CPU_MS}`, weight: 2 },
  { name: 'PerfSleep', url: `/API/PerfSleep?ms=${SLEEP_MS}`, weight: 1 },
  { name: 'PerfJson', url: `/API/PerfJson?n=${JSON_N}`, weight: 2 },
];

const active = ONLY ? endpoints.filter((e) => e.name === ONLY) : endpoints;
if (active.length === 0) throw new Error(`ONLY=${ONLY} matched no endpoint`);

// Weighted pick list (each endpoint repeated `weight` times); single-endpoint mode picks it every time.
const pick = [];
for (const e of active) {
  const n = ONLY ? 1 : e.weight;
  for (let i = 0; i < n; i++) pick.push(e);
}

// Per-endpoint latency trends so the summary breaks latency down by endpoint.
const lat = {};
for (const e of endpoints) lat[e.name] = new Trend(`lat_${e.name}`, true);

export const options = {
  scenarios:
    RATE > 0
      ? {
          fixed_rate: {
            executor: 'constant-arrival-rate',
            rate: RATE,
            timeUnit: '1s',
            duration: DURATION,
            preAllocatedVUs: 50,
            maxVUs: 400,
          },
        }
      : {
          open: {
            executor: 'constant-vus',
            vus: VUS,
            duration: DURATION,
          },
        },
  discardResponseBodies: false, // keep bodies so we can assert the "ok":true marker
  thresholds: {
    http_req_failed: ['rate<0.01'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const e = pick[Math.floor(Math.random() * pick.length)];
  const res = http.get(BASE + e.url, { tags: { name: e.name } });
  check(res, {
    'status 200': (r) => r.status === 200,
    'ok body': (r) => typeof r.body === 'string' && r.body.indexOf('"ok":true') !== -1,
  });
  lat[e.name].add(res.timings.duration);
}
