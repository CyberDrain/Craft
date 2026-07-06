// k6 static-content load test for the CRAFT origin.
//
// Drives concurrent traffic at a representative set of static assets and records
// per-asset TTFB + throughput + error rate. Targets are discovered at runtime by
// run.ps1 and written to /out/targets.json (so hashed filenames stay correct across
// rebuilds). Bodies are discarded (the 23.5 MB chunk would otherwise blow up memory);
// we still get wire bytes via data_received and headers via checks.
//
// Env:
//   BASE      base URL (default http://sut:8080)
//   VUS       virtual users (default 10)
//   DURATION  test duration (default 30s)
//
// Output: --summary-export /out/k6-summary.json (written by the docker run invocation)

import http from 'k6/http';
import { check } from 'k6';
import { Trend, Counter } from 'k6/metrics';

const BASE = __ENV.BASE || 'http://sut:8080';
const targets = JSON.parse(open('/out/targets.json'));

// Per-asset-kind TTFB trends so the summary breaks the latency down by what was served.
const ttfb = {
  immutable_js: new Trend('ttfb_immutable_js', true),
  css: new Trend('ttfb_css', true),
  html: new Trend('ttfb_html', true),
  data_json: new Trend('ttfb_data_json', true),
  image: new Trend('ttfb_image', true),
};
const served_br = new Counter('served_brotli');
const served_gzip = new Counter('served_gzip');
const served_identity = new Counter('served_identity');
const chunked = new Counter('responses_chunked'); // no Content-Length => on-the-fly compression

const RATE = parseInt(__ENV.RATE || '0', 10);
const DURATION = __ENV.DURATION || '30s';

export const options = {
  // RATE>0: drive a constant offered load (iterations/sec) so CPU at that fixed load is comparable
  // before/after. RATE=0: open throttle (constant VUs) to measure peak throughput/latency.
  scenarios: RATE > 0
    ? {
        fixed_rate: {
          executor: 'constant-arrival-rate',
          rate: RATE, timeUnit: '1s', duration: DURATION,
          preAllocatedVUs: 50, maxVUs: 300,
        },
      }
    : {
        static_mix: {
          executor: 'constant-vus',
          vus: parseInt(__ENV.VUS || '10', 10),
          duration: DURATION,
        },
      },
  discardResponseBodies: true,
  thresholds: {
    http_req_failed: ['rate<0.01'],          // <1% errors
    http_req_duration: ['p(95)<2000'],        // sanity ceiling
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'max'],
};

export default function () {
  for (const t of targets) {
    const res = http.get(BASE + t.url, {
      headers: { 'Accept-Encoding': 'br, gzip' },
      tags: { name: t.url, kind: t.kind },
    });

    check(res, {
      'status 200': (r) => r.status === 200,
      'has cache-control': (r) => !!r.headers['Cache-Control'],
      'no set-cookie on asset': (r) => !r.headers['Set-Cookie'],
    });

    if (ttfb[t.kind]) ttfb[t.kind].add(res.timings.waiting);

    const enc = (res.headers['Content-Encoding'] || '').toLowerCase();
    if (enc.includes('br')) served_br.add(1);
    else if (enc.includes('gzip')) served_gzip.add(1);
    else served_identity.add(1);

    if (!res.headers['Content-Length']) chunked.add(1);
  }
}
