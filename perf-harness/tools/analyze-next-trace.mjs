// Analyze a Next.js .next/trace file to find what dominates the build. Aggregates spans by name
// (self time = duration minus children, the real hotspot signal; plus wall/total), and lists the
// slowest individual spans with their tags (page path, module, etc).
//   node analyze-next-trace.mjs <path-to-.next/trace>
// Next writes durations in microseconds. The file is either one JSON array or NDJSON (one array per line).
import { readFileSync } from "node:fs";

const file = process.argv[2] || ".next/trace";
const raw = readFileSync(file, "utf8").trim();

let spans = [];
try {
  const j = JSON.parse(raw);
  spans = Array.isArray(j) ? j.flat() : [j];
} catch {
  for (const line of raw.split("\n")) {
    const t = line.trim();
    if (!t) continue;
    try {
      const arr = JSON.parse(t);
      Array.isArray(arr) ? spans.push(...arr) : spans.push(arr);
    } catch {}
  }
}
spans = spans.filter((s) => s && s.name);

// children duration per parent id, for self-time
const childDur = new Map();
for (const s of spans) {
  if (s.parentId != null) childDur.set(s.parentId, (childDur.get(s.parentId) || 0) + (s.duration || 0));
}

const agg = new Map();
for (const s of spans) {
  const dur = s.duration || 0;
  const self = Math.max(0, dur - (childDur.get(s.id) || 0));
  const a = agg.get(s.name) || { count: 0, total: 0, self: 0 };
  a.count++; a.total += dur; a.self += self;
  agg.set(s.name, a);
}

const s1 = (us) => (us / 1e6).toFixed(1) + "s"; // microseconds -> seconds

// sanity: the largest single span ~= whole build, confirms microsecond units
const maxSpan = spans.reduce((m, s) => ((s.duration || 0) > (m.duration || 0) ? s : m), {});
console.log(`spans=${spans.length}  longest single span: ${s1(maxSpan.duration || 0)} (${maxSpan.name})`);

console.log(`\n--- span names by SELF time (where time is actually spent) ---`);
[...agg.entries()]
  .map(([name, a]) => ({ name, ...a }))
  .sort((a, b) => b.self - a.self)
  .slice(0, 25)
  .forEach((r) => console.log(`  self ${s1(r.self).padStart(8)}  total ${s1(r.total).padStart(8)}  x${String(r.count).padStart(5)}  ${r.name}`));

console.log(`\n--- slowest individual spans (specific operations) ---`);
spans
  .filter((s) => s.duration != null)
  .sort((a, b) => b.duration - a.duration)
  .slice(0, 22)
  .forEach((s) => {
    const tag = s.tags ? JSON.stringify(s.tags).slice(0, 100) : "";
    console.log(`  ${s1(s.duration).padStart(8)}  ${s.name}  ${tag}`);
  });
