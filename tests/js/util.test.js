// Chạy: node --test tests/js/util.test.js
const test = require("node:test");
const assert = require("node:assert/strict");
const { esc, safeUrl, toInt } = require("../../src/ThreadsToxic.Web/wwwroot/js/util.js");

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
