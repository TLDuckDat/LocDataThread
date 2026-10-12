// Tiện ích thuần (không phụ thuộc DOM) để chèn dữ liệu không tin cậy vào HTML.
// Dùng được cả trong trình duyệt (window.ThreadsUtil) lẫn Node (require) để kiểm thử.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.ThreadsUtil = factory();
})(typeof self !== "undefined" ? self : this, function () {
  const HTML_ESCAPES = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;", "`": "&#96;" };

  // Escape cho cả nội dung văn bản lẫn giá trị thuộc tính đặt trong dấu nháy.
  function esc(value) {
    return String(value ?? "").replace(/[&<>"'`]/g, (c) => HTML_ESCAPES[c]);
  }

  // Chỉ cho phép liên kết tuyệt đối http(s). Trả về chuỗi rỗng nếu không an toàn
  // (javascript:, data:, vbscript:, đường dẫn tương đối, chuỗi rác...).
  function safeUrl(raw) {
    if (typeof raw !== "string") return "";
    try {
      const url = new URL(raw.trim());
      return url.protocol === "http:" || url.protocol === "https:" ? url.href : "";
    } catch {
      return "";
    }
  }

  // Ép về số nguyên không âm (dùng cho giá trị số chèn vào HTML như cấp F0-F3).
  function toInt(value, fallback = 0) {
    const n = Number(value);
    return Number.isFinite(n) ? Math.max(0, Math.trunc(n)) : fallback;
  }

  return { esc, safeUrl, toInt };
});
