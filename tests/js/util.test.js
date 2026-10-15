// Chạy: node --test tests/js/util.test.js
const test = require("node:test");
const assert = require("node:assert/strict");
const { esc, safeUrl, toInt, createPageCache } = require("../../src/ThreadsToxic.Web/wwwroot/js/util.js");

test("esc neutralises markup in text content", () => {
  assert.equal(esc("<img src=x onerror=alert(1)>"), "&lt;img src=x onerror=alert(1)&gt;");
  assert.equal(esc("<script>alert(1)</script>"), "&lt;script&gt;alert(1)&lt;/script&gt;");
});

test("esc is safe inside double- and single-quoted attributes", () => {
  const payload = `" onmouseover="alert(1)`;
  assert.ok(!esc(payload).includes('"'));
  const single = `' onmouseover='alert(1)`;
  assert.ok(!esc(single).includes("'"));
  assert.ok(!esc("`x`").includes("`"));
});

test("esc escapes ampersands first so entities cannot be smuggled", () => {
  assert.equal(esc("&lt;"), "&amp;lt;");
});

test("esc tolerates null, undefined and numbers", () => {
  assert.equal(esc(null), "");
  assert.equal(esc(undefined), "");
  assert.equal(esc(42), "42");
});

test("safeUrl allows only absolute http(s) links", () => {
  assert.equal(safeUrl("https://www.threads.com/@a/post/1"), "https://www.threads.com/@a/post/1");
  assert.equal(safeUrl("http://example.test/x"), "http://example.test/x");
  assert.equal(safeUrl("  https://example.test/x  "), "https://example.test/x");
});

test("safeUrl rejects script-capable and malformed URLs", () => {
  for (const bad of [
    "javascript:alert(1)",
    "JaVaScRiPt:alert(1)",
    "java\tscript:alert(1)",
    "java\nscript:alert(1)",
    " javascript:alert(1)",
    "data:text/html,<script>alert(1)</script>",
    "vbscript:msgbox(1)",
    "file:///c:/windows/win.ini",
    "//evil.example/x",
    "/relative/path",
    "not a url",
    "",
  ]) {
    assert.equal(safeUrl(bad), "", `phải chặn: ${JSON.stringify(bad)}`);
  }
});

test("safeUrl rejects non-strings", () => {
  for (const bad of [null, undefined, 42, {}, ["https://a.test"]]) {
    assert.equal(safeUrl(bad), "");
  }
});

test("toInt coerces untrusted numbers", () => {
  assert.equal(toInt("2"), 2);
  assert.equal(toInt("<img>"), 0);
  assert.equal(toInt(-3), 0);
  assert.equal(toInt(2.9), 2);
  assert.equal(toInt(undefined, 1), 1);
});

// ---------- createPageCache ----------
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
};

test("createPageCache reuses a prefetched page, even while it is still loading", async () => {
  const cache = createPageCache();
  const pending = deferred();
  let calls = 0;
  cache.prefetch("page=2", () => { calls++; return pending.promise; });

  const got = cache.get("page=2", () => { calls++; return Promise.resolve("fresh"); });
  pending.resolve("prefetched");
  assert.equal(await got, "prefetched");
  assert.equal(calls, 1);
});

test("createPageCache uses each prefetched page only once", async () => {
  const cache = createPageCache();
  cache.prefetch("page=2", () => Promise.resolve("prefetched"));
  await cache.get("page=2", () => Promise.resolve("fresh"));
  assert.equal(await cache.get("page=2", () => Promise.resolve("fresh")), "fresh");
});

test("createPageCache ignores stale or cleared entries", async () => {
  let clock = 0;
  const cache = createPageCache(1000, () => clock);
  cache.prefetch("page=2", () => Promise.resolve("old"));
  clock = 1001;
  assert.equal(await cache.get("page=2", () => Promise.resolve("fresh")), "fresh");

  cache.prefetch("page=3", () => Promise.resolve("old"));
  cache.clear();     // dữ liệu vừa đổi
  assert.equal(cache.size, 0);
  assert.equal(await cache.get("page=3", () => Promise.resolve("fresh")), "fresh");
});

test("createPageCache falls back to a fresh load when the prefetch failed", async () => {
  const cache = createPageCache();
  cache.prefetch("page=2", () => Promise.reject(new Error("offline")));
  assert.equal(await cache.get("page=2", () => Promise.resolve("fresh")), "fresh");
});

test("createPageCache does not prefetch a page that is already fresh", () => {
  const cache = createPageCache();
  let calls = 0;
  const load = () => { calls++; return Promise.resolve("x"); };
  cache.prefetch("page=2", load);
  cache.prefetch("page=2", load);
  assert.equal(calls, 1);
});
