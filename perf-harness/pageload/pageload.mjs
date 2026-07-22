// Per-page browser load test. Loads each real route in a fresh (cold-cache) Chromium context against
// the running SUT and records: wire transfer (Content-Length of each response — accurate for our
// precompressed assets), request/JS counts, and FCP / LCP / DOMContentLoaded / load timings, plus the
// per-page request waterfall. Output: /work/pageload-results.json.
import { chromium } from "playwright";
import { readFileSync, writeFileSync } from "node:fs";

const BASE = process.env.BASE || "http://sut:8080";
const SETTLE_MS = parseInt(process.env.SETTLE_MS || "4000", 10); // let lazy route chunks + LCP settle
const routes = JSON.parse(readFileSync("/work/routes.json", "utf8"));

const browser = await chromium.launch({ args: ["--no-sandbox", "--disable-dev-shm-usage"] });
const results = [];

for (const route of routes) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true }); // fresh = cold cache; accept caddy's internal cert
  const page = await context.newPage();
  const reqs = [];
  page.on("response", (resp) => {
    try {
      const h = resp.headers();
      reqs.push({
        url: resp.url().replace(BASE, ""),
        status: resp.status(),
        type: resp.request().resourceType(),
        enc: h["content-encoding"] || "",
        size: parseInt(h["content-length"] || "0", 10),
      });
    } catch {}
  });

  let navOk = true;
  try {
    await page.goto(BASE + route, { waitUntil: "load", timeout: 30000 });
  } catch {
    navOk = false;
  }
  await page.waitForTimeout(SETTLE_MS);

  const timings = await page.evaluate(() => {
    const nav = performance.getEntriesByType("navigation")[0] || {};
    const fcp = performance.getEntriesByName("first-contentful-paint")[0]?.startTime;
    const lcpE = performance.getEntriesByType("largest-contentful-paint");
    return {
      dcl: nav.domContentLoadedEventEnd,
      load: nav.loadEventEnd,
      fcp,
      lcp: lcpE.length ? lcpE[lcpE.length - 1].startTime : undefined,
    };
  });

  const js = reqs.filter((r) => r.type === "script");
  const totalBytes = reqs.reduce((s, r) => s + (r.size || 0), 0);
  const jsBytes = js.reduce((s, r) => s + (r.size || 0), 0);
  const top = [...reqs].sort((a, b) => b.size - a.size).slice(0, 6);

  const row = {
    route,
    navOk,
    requests: reqs.length,
    jsRequests: js.length,
    totalKB: Math.round(totalBytes / 1024),
    jsKB: Math.round(jsBytes / 1024),
    fcpMs: Math.round(timings.fcp || 0),
    lcpMs: Math.round(timings.lcp || 0),
    dclMs: Math.round(timings.dcl || 0),
    loadMs: Math.round(timings.load || 0),
    topRequests: top.map((r) => ({ url: r.url, kb: Math.round(r.size / 1024), enc: r.enc, type: r.type })),
  };
  results.push(row);
  console.log(
    `${route.padEnd(42)} req=${String(row.requests).padStart(3)} total=${String(row.totalKB).padStart(5)}KB ` +
    `js=${String(row.jsKB).padStart(5)}KB FCP=${String(row.fcpMs).padStart(5)}ms LCP=${String(row.lcpMs).padStart(5)}ms`
  );
  await context.close();
}

await browser.close();
writeFileSync("/work/pageload-results.json", JSON.stringify(results, null, 2));
console.log(`\n[pageload] wrote ${results.length} routes to pageload-results.json`);
