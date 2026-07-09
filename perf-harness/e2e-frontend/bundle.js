// CRAFT E2E static bundle — marker: E2E_BUNDLE_MARKER
// A deliberately compressible file so a precompressed .br sibling is meaningfully smaller than the
// identity file; the E2E harness confirms CRAFT serves the .br with Content-Encoding: br when the
// client accepts brotli, and the identity file otherwise.
(function () {
  "use strict";

  var CRAFT_E2E = {
    marker: "E2E_BUNDLE_MARKER",
    version: "1.0.0",
    features: ["frontend", "http", "background", "orchestrator", "scheduler", "realtime"],
  };

  // Repetitive, highly-compressible payload so brotli has plenty to work with.
  var samples = [];
  for (var i = 0; i < 200; i++) {
    samples.push({
      index: i,
      name: "sample-item-number-" + i,
      description: "this is a repeated, highly compressible description string for the e2e bundle",
      category: "category-" + (i % 8),
      active: (i % 2) === 0,
      tags: ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"],
    });
  }

  function summarize(items) {
    return items.reduce(function (acc, item) {
      acc.total += 1;
      acc.active += item.active ? 1 : 0;
      return acc;
    }, { total: 0, active: 0 });
  }

  CRAFT_E2E.summary = summarize(samples);

  if (typeof window !== "undefined") {
    window.CRAFT_E2E = CRAFT_E2E;
  }
})();
