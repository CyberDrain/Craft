// Pre-compress static assets: Brotli q11 + gzip 9, emitted alongside the originals so the origin
// serves precompressed files with zero per-request compression CPU. Parallelized across all available
// CPU cores (worker_threads + an atomic work queue) so it scales on multi-core CI runners. Node 22's
// zlib has Brotli built in — no extra dependency. This mirrors CIPP-NG/build/tools/precompress.mjs.
//   node precompress.mjs <dir>
import { readdirSync, readFileSync, writeFileSync, statSync } from "node:fs";
import { join, extname } from "node:path";
import { brotliCompressSync, gzipSync, constants } from "node:zlib";
import { availableParallelism } from "node:os";
import { Worker, isMainThread, parentPort, workerData } from "node:worker_threads";

const COMPRESSIBLE = new Set([".js", ".css", ".html", ".json", ".svg", ".xml", ".txt", ".map", ".wasm"]);
// Compress every compressible file regardless of size; the per-file guard in compressOne() only KEEPS
// a .br/.gz when it's actually smaller than the original, so the origin never serves a precompressed
// file larger than raw (brotli/gzip framing can exceed a tiny input — e.g. a 57-byte JSON). Build time
// is irrelevant here; serve speed is the goal. The 1-byte floor just skips empty files.
const MIN_BYTES = 1;

function compressOne(p) {
  const buf = readFileSync(p);
  if (buf.length < MIN_BYTES) return null;
  const br = brotliCompressSync(buf, {
    params: {
      [constants.BROTLI_PARAM_QUALITY]: 11,
      [constants.BROTLI_PARAM_SIZE_HINT]: buf.length,
    },
  });
  const gz = gzipSync(buf, { level: 9 });
  // Only emit a sibling that beats the raw file. If neither does (tiny inputs), write nothing and the
  // origin serves the original identity with a fixed Content-Length — never a larger precompressed file.
  let br_ = 0, gz_ = 0;
  if (br.length < buf.length) { writeFileSync(p + ".br", br); br_ = br.length; }
  if (gz.length < buf.length) { writeFileSync(p + ".gz", gz); gz_ = gz.length; }
  return { raw: buf.length, br: br_, gz: gz_, kept: (br_ > 0 || gz_ > 0) };
}

if (!isMainThread) {
  const { files, counter } = workerData;
  const res = { files: 0, raw: 0, br: 0, gz: 0 };
  let i;
  while ((i = Atomics.add(counter, 0, 1)) < files.length) {
    const r = compressOne(files[i]);
    if (r && r.kept) { res.files++; res.raw += r.raw; res.br += r.br; res.gz += r.gz; }
  }
  parentPort.postMessage(res);
} else {
  const root = process.argv[2] || "./out";
  const files = [];
  (function walk(dir) {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = join(dir, e.name);
      if (e.isDirectory()) { walk(p); continue; }
      if (e.name.endsWith(".br") || e.name.endsWith(".gz")) continue;
      if (!COMPRESSIBLE.has(extname(e.name).toLowerCase())) continue;
      files.push(p);
    }
  })(root);
  files.sort((a, b) => statSync(b).size - statSync(a).size);

  const workers = Math.max(1, Math.min(availableParallelism(), files.length || 1));
  const counter = new Int32Array(new SharedArrayBuffer(4));
  const totals = { files: 0, raw: 0, br: 0, gz: 0 };
  const t0 = Date.now();
  let exited = 0;

  if (files.length === 0) {
    console.log("[precompress] 0 files");
  } else {
    for (let w = 0; w < workers; w++) {
      const worker = new Worker(new URL(import.meta.url), { workerData: { files, counter } });
      worker.on("message", (r) => { totals.files += r.files; totals.raw += r.raw; totals.br += r.br; totals.gz += r.gz; });
      worker.on("error", (err) => { console.error("[precompress] worker error:", err); process.exitCode = 1; });
      worker.on("exit", () => {
        if (++exited === workers) {
          console.log(
            `[precompress] ${totals.files} files  raw ${(totals.raw / 1e6).toFixed(1)}MB -> ` +
            `br ${(totals.br / 1e6).toFixed(1)}MB / gz ${(totals.gz / 1e6).toFixed(1)}MB  ` +
            `across ${workers} core(s) in ${((Date.now() - t0) / 1000).toFixed(1)}s`
          );
        }
      });
    }
  }
}
