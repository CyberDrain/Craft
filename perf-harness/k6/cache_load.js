// k6 load for the CRAFT response cache + auth header middleware (frontend+http mode).
// Every request carries an x-ms-client-principal (SWA format) so the auth middleware processes it and the
// cache keys on the user's roles. Hits /API/ListPerf (the "List" prefix makes the cache engage).
//
// Env:
//   BASE      base URL (default http://sut:8080)
//   VUS       virtual users (default 20)
//   DURATION  test duration (default 20s)
//   N         items the endpoint returns (default 50)
//   MODE      hit  = fixed query (all cache hits after the first)  |  miss = unique query per iter (all misses)

import http from 'k6/http';
import { check } from 'k6';
import encoding from 'k6/encoding';

const BASE = __ENV.BASE || 'http://sut:8080';
const VUS = parseInt(__ENV.VUS || '20', 10);
const DURATION = __ENV.DURATION || '20s';
const N = __ENV.N || '50';
const MODE = __ENV.MODE || 'hit';

// SWA-format principal (has userRoles) → auth middleware passes it through (the common case).
const principal = encoding.b64encode(
  JSON.stringify({
    identityProvider: 'aad',
    userId: 'perf-user-1',
    userDetails: 'perf@test.local',
    userRoles: ['admin', 'editor', 'reader'],
  })
);

export const options = {
  scenarios: { open: { executor: 'constant-vus', vus: VUS, duration: DURATION } },
  thresholds: { http_req_failed: ['rate<0.01'] },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  // miss mode: a unique cache-buster per iteration so every request misses (measures PS invoke + cache Set).
  const q = MODE === 'miss' ? `&cb=${__VU}-${__ITER}` : '';
  const res = http.get(`${BASE}/API/ListPerf?n=${N}${q}`, {
    headers: { 'x-ms-client-principal': principal },
    tags: { mode: MODE },
  });
  check(res, {
    'status 200': (r) => r.status === 200,
    'x-cache present': (r) => !!r.headers['X-Cache'],
  });
}
