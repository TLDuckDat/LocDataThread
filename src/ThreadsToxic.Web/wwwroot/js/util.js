// Tiện ích thuần (không phụ thuộc DOM): chèn dữ liệu không tin cậy vào HTML, bộ đệm trang tải trước.
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

  // Bộ đệm trang tải trước (vd. trang kế tiếp) để chuyển trang không phải chờ một lượt đi về server.
  // Khóa là chuỗi tham số; lưu promise nên request tải trước còn đang chạy cũng được dùng lại.
  // Bản tải trước dùng một lần, quá maxAgeMs thì bỏ; clear() khi dữ liệu vừa đổi.
  function createPageCache(maxAgeMs = 15000, now = () => Date.now()) {
    const entries = new Map();   // key -> { promise, at }
    const fresh = (entry) => entry && now() - entry.at <= maxAgeMs;

    return {
      // Lấy trang: dùng bản tải trước nếu còn mới (lỗi thì tải lại), ngược lại gọi load(key).
      get(key, load) {
        const entry = entries.get(key);
        entries.delete(key);
        return fresh(entry) ? entry.promise.catch(() => load(key)) : load(key);
      },
      // Tải trước ở nền; lỗi không báo ra ngoài (lần get sau tự tải lại).
      prefetch(key, load) {
        if (fresh(entries.get(key))) return;
        const promise = load(key);
        promise.catch(() => {
          if (entries.get(key)?.promise === promise) entries.delete(key);
        });
        entries.set(key, { promise, at: now() });
      },
      clear() { entries.clear(); },
      get size() { return entries.size; },
    };
  }

  return { esc, safeUrl, toInt, createPageCache };
});
