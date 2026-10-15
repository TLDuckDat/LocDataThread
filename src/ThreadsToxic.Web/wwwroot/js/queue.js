// Kho phụ: bản ghi từ threads_review.db chờ người dùng quyết định.
// Đưa vào CSDL chính (tạo bình luận trong "Đang lọc") hoặc loại. Thời gian thực qua SignalR.
(() => {
  const PAGE_SIZE = 50;
  const NAME_KEY = "threads-toxic-name";
  const REASON_LABELS = {
    duplicate_id: "Trùng id",
    duplicate_content: "Trùng nội dung",
    non_vietnamese: "Không phải tiếng Việt",
    meta_ai: "Meta AI",
    duplicate: "Trùng",
  };
  const reasonLabel = (r) => REASON_LABELS[r] || r;

  const state = {
    me: null,
    token: null,
    decision: "pending",
    page: 1,
    total: 0,
    items: [],
    counts: { pending: 0, pendingInMain: 0, promoted: 0, rejected: 0, reasons: [] },
    selected: new Set(),
    focusId: null,
    connected: false,
  };

  let hub = null;
  const $ = (sel) => document.querySelector(sel);
  const { esc, toInt, createPageCache } = window.ThreadsUtil;

  const debounce = (fn, ms) => {
    let timer = null;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => fn(...args), ms);
    };
  };

  const formatDate = (raw) => {
    if (!raw) return "—";
    const d = new Date(raw);
    return Number.isNaN(d.getTime()) ? raw : d.toLocaleString("vi-VN");
  };

  const toast = (message, ms = 4000) => {
    const el = document.createElement("div");
    el.className = "toast";
    el.textContent = message;
    $("#toasts").append(el);
    setTimeout(() => el.remove(), ms);
  };

  const storage = {
    get() { try { return localStorage.getItem(NAME_KEY); } catch { return null; } },
    set(v) { try { localStorage.setItem(NAME_KEY, v); } catch { /* chế độ riêng tư: bỏ qua */ } },
  };

  async function api(path, options = {}) {
    const res = await fetch(path, {
      ...options,
      headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    });
    const body = await res.json().catch(() => null);
    if (!res.ok) {
      const error = new Error(typeof body === "string" ? body : "Yêu cầu thất bại.");
      error.status = res.status;
      error.body = body;
      throw error;
    }
    return body;
  }

  // Ghi bằng token phiên; token hết hạn (401) thì Join lại và thử đúng một lần.
  async function postWithSession(path, body, canRetry = true) {
    try {
      return await api(path, {
        method: "POST",
        headers: { "X-Session-Token": state.token || "" },
        body: JSON.stringify(body),
      });
    } catch (err) {
      if (err.status === 401 && canRetry) {
        await joinRoom();
        return postWithSession(path, body, false);
      }
      throw err;
    }
  }

  // ---------- Tải & hiển thị ----------
  function params() {
    const p = new URLSearchParams({ decision: state.decision, page: state.page, pageSize: PAGE_SIZE });
    const map = { type: "#type", reason: "#reason", inMain: "#inMain", keyword: "#keyword" };
    for (const [key, sel] of Object.entries(map)) {
      const value = $(sel).value.trim();
      if (value) p.set(key, value);
    }
    return p;
  }

  let reloadSeq = 0;

  // Trang kế tiếp được tải trước ở nền nên bấm "Sau →" hiện ngay (xem app.js)
  const pageCache = createPageCache();
  const fetchPage = (query) => api(`/api/review?${query}`);

  function prefetchNextPage() {
    if (state.page >= Math.ceil(state.total / PAGE_SIZE)) return;
    const next = params();
    next.set("page", String(state.page + 1));
    pageCache.prefetch(next.toString(), fetchPage);
  }

  function setLoading(on) {
    $("#list").classList.toggle("is-loading", on);
    $("#list").setAttribute("aria-busy", String(on));
  }

  async function reload() {
    const seq = ++reloadSeq;
    const loadingTimer = setTimeout(() => { if (seq === reloadSeq) setLoading(true); }, 150);
    try {
      const data = await pageCache.get(params().toString(), fetchPage);
      if (seq !== reloadSeq) return;
      state.total = data.total;
      state.items = data.items;
      state.counts = data.counts;
      const visible = new Set(data.items.map((i) => i.id));
      for (const id of [...state.selected]) if (!visible.has(id)) state.selected.delete(id);
      if (state.focusId && !visible.has(state.focusId)) state.focusId = null;
      renderReasons();
      renderCounts();
      renderList();
      renderPager();
      prefetchNextPage();
    } catch (err) {
      if (seq !== reloadSeq) return;
      toast(`Không tải được dữ liệu: ${err.message}`);
    } finally {
      clearTimeout(loadingTimer);
      if (seq === reloadSeq) setLoading(false);
    }
  }
  const debouncedReload = debounce(reload, 150);

  // Dữ liệu vừa đổi: bỏ các trang tải trước rồi tải lại
  function scheduleReload() {
    pageCache.clear();
    debouncedReload();
  }

  function renderReasons() {
    const select = $("#reason");
    const current = select.value;
    const options = state.counts.reasons.map((r) =>
      `<option value="${esc(r.reason)}">${esc(reasonLabel(r.reason))} · ${esc(r.recordType === "post" ? "bài viết" : "bình luận")} (${r.count.toLocaleString("vi-VN")})</option>`
    ).join("");
    select.innerHTML = `<option value="">Mọi lý do</option>${options}`;
    if ([...select.options].some((o) => o.value === current)) select.value = current;
  }

  function renderCounts() {
    const c = state.counts;
    $("#countPending").textContent = c.pending.toLocaleString("vi-VN");
    $("#countPromoted").textContent = c.promoted.toLocaleString("vi-VN");
    $("#countRejected").textContent = c.rejected.toLocaleString("vi-VN");
    $("#summary").innerHTML =
      `Còn <b>${c.pending.toLocaleString("vi-VN")}</b> bản ghi chờ quyết định. ` +
      `<b>${c.pendingInMain.toLocaleString("vi-VN")}</b> bản ghi đã có trong CSDL chính đã được tự ẩn. ` +
      `Đưa vào CSDL chính thì bình luận sẽ nằm trong mục <b>Đang lọc</b> để cả nhóm xử lý tiếp.`;
    document.querySelectorAll(".tab").forEach((tab) =>
      tab.classList.toggle("is-active", tab.dataset.decision === state.decision));

    const pendingView = state.decision === "pending";
    $("#promoteBtn").hidden = !pendingView;
    $("#rejectBtn").hidden = !pendingView;
    $("#bulkBox").hidden = !pendingView || state.total === 0;
    $("#bulkCount").textContent = state.total.toLocaleString("vi-VN");
    $("#selCount").textContent = `Đã chọn ${state.selected.size}`;
    $("#selectPage").checked = state.items.length > 0 && state.items.every((i) => state.selected.has(i.id));
  }

  function rowHtml(item) {
    const classes = ["row"];
    if (item.id === state.focusId) classes.push("is-focus");
    if (state.selected.has(item.id)) classes.push("is-selected");

    const kind = item.recordType === "post" ? "Bài viết" : "Bình luận";
    const level = toInt(item.replyLevel);
    const chain = item.recordType === "comment" && level > 0
      ? [item.f0, item.f1, item.f2, item.f3].slice(0, level + 1)
        .map((text, idx) => `<li class="${idx === level ? "current" : ""}"><small>F${idx}</small>${esc(text)}</li>`).join("")
      : "";
    const decided = item.decision === "pending" ? "" :
      `<span class="muted">${item.decision === "promoted" ? "Đã đưa vào CSDL chính" : "Đã loại"} bởi ${esc(item.decidedBy || "—")} · ${esc(formatDate(item.decidedAt))}</span>`;
    const undo = item.decision === "rejected" && item.decidedBy === state.me?.name
      ? `<button class="btn ghost" type="button" data-role="restore" data-id="${esc(item.id)}">Hoàn tác loại</button>` : "";

    return `
      <article class="${classes.join(" ")}" data-id="${esc(item.id)}">
        <label class="check"><input type="checkbox" data-role="select" ${state.selected.has(item.id) ? "checked" : ""} aria-label="Chọn bản ghi"></label>
        <div>
          <div class="meta">
            <span class="level">${kind}</span>
            <span class="source">${esc(reasonLabel(item.reason))}</span>
            ${item.occurrences > 1 ? `<span title="Số lần gặp">×${item.occurrences}</span>` : ""}
            <span class="score ${item.toxicScore >= 0.7 ? "high" : item.toxicScore >= 0.4 ? "mid" : ""}">${Math.round(item.toxicScore * 100)}%</span>
            ${level ? `<span class="level">F${level}</span>` : ""}
            <span>@${esc(item.authorUsername || "ẩn danh")}</span>
            ${item.existsInMain ? `<span class="muted">· đã có trong CSDL chính</span>` : ""}
            ${decided}
            ${undo}
          </div>
          <p class="content">${esc(item.content)}</p>
          ${chain ? `<ol class="chain">${chain}</ol>` : ""}
          ${item.postUrl ? `<div class="muted small">${esc(item.postUrl)}</div>` : ""}
        </div>
      </article>`;
  }

  function renderList() {
    $("#list").innerHTML = state.items.length
      ? state.items.map(rowHtml).join("")
      : `<div class="empty">${state.decision === "pending"
        ? "Không còn bản ghi nào chờ quyết định với bộ lọc này."
        : "Chưa có bản ghi nào trong mục này."}</div>`;
  }

  function renderPager() {
    const pages = Math.max(1, Math.ceil(state.total / PAGE_SIZE));
    $("#pageInfo").textContent = `Trang ${state.page}/${pages} · ${state.total.toLocaleString("vi-VN")} bản ghi`;
    $("#prev").disabled = state.page <= 1;
    $("#next").disabled = state.page >= pages;
  }

  // ---------- Hành động ----------
  function describe(result) {
    const parts = [];
    if (result.promotedComments) parts.push(`${result.promotedComments} bình luận`);
    if (result.promotedPosts) parts.push(`${result.promotedPosts} bài viết`);
    let message = parts.length ? `Đã đưa vào CSDL chính: ${parts.join(", ")}.` : "";
    if (result.rejected) message += ` Đã loại ${result.rejected}.`;
    if (result.restored) message += ` Đã hoàn tác ${result.restored}.`;
    if (result.skippedInMain) message += ` ${result.skippedInMain} bản ghi vừa có trong CSDL chính nên tự ẩn.`;
    return message.trim() || "Không có bản ghi nào thay đổi.";
  }

  async function act(path, ids, label) {
    if (!ids.length) return;
    try {
      const result = await postWithSession(path, { ids });
      toast(describe(result), 6000);
      for (const id of ids) state.selected.delete(id);
      if (state.focusId && ids.includes(state.focusId)) state.focusId = null;
    } catch (err) {
      toast(`${label} thất bại: ${err.message}`, 6000);
    }
    scheduleReload();
  }

  const selectedOrFocus = () =>
    state.selected.size ? [...state.selected] : (state.focusId ? [state.focusId] : []);

  // Hàng loạt: số lượng xác nhận phải khớp với số đang hiển thị; sai lệch thì server từ chối
  async function bulk(action) {
    const verb = action === "promote" ? "đưa vào CSDL chính" : "loại";
    const ok = window.confirm(
      `Bạn sắp ${verb} ${state.total.toLocaleString("vi-VN")} bản ghi khớp bộ lọc hiện tại.\n\n` +
      "Thao tác này áp dụng cho TOÀN BỘ kết quả, không chỉ trang đang xem. Tiếp tục?");
    if (!ok) return;
    const body = {
      action,
      type: $("#type").value || null,
      reason: $("#reason").value || null,
      keyword: $("#keyword").value.trim() || null,
      expectedCount: state.total,
    };
    try {
      const result = await postWithSession("/api/review/bulk", body);
      toast(describe(result), 8000);
      state.selected.clear();
    } catch (err) {
      if (err.status === 409) toast("Dữ liệu đã thay đổi (có người khác vừa xử lý). Danh sách đã được tải lại, hãy kiểm tra rồi thử lại.", 8000);
      else toast(`Thao tác hàng loạt thất bại: ${err.message}`, 6000);
    }
    scheduleReload();
  }

  function moveFocusAt(offset) {
    if (!state.items.length) return;
    const idx = state.items.findIndex((i) => i.id === state.focusId);
    const next = Math.min(state.items.length - 1, Math.max(0, idx + offset));
    state.focusId = state.items[next].id;
    renderList();
    document.querySelector(`.row[data-id="${CSS.escape(state.focusId)}"]`)?.scrollIntoView({ block: "nearest" });
  }

  function toggleSelect(id) {
    if (state.selected.has(id)) state.selected.delete(id);
    else state.selected.add(id);
    renderCounts();
    renderList();
  }

  // ---------- SignalR ----------
  function connectHub() {
    hub = new signalR.HubConnectionBuilder().withUrl("/hubs/collab").withAutomaticReconnect().build();
    hub.on("QueueChanged", ({ by }) => {
      if (by && by !== state.me.name) toast(`${by} vừa xử lý kho phụ.`);
      scheduleReload();
    });
    hub.on("PresenceChanged", (list) => {
      $("#presence").innerHTML = list.map((p) => `<span class="chip">${esc(p.name)}</span>`).join("");
    });
    hub.onreconnecting(() => { state.connected = false; state.token = null; setStatus("Đang kết nối lại…", "connecting"); });
    hub.onreconnected(async () => { await joinRoom(); scheduleReload(); });
    hub.onclose(() => { state.connected = false; state.token = null; setStatus("Mất kết nối", "offline"); });
  }

  async function joinRoom() {
    const result = await hub.invoke("Join", state.me.name);
    state.token = result.token;
    state.connected = true;
    setStatus("Đã kết nối", "online");
  }

  function setStatus(text, stateName) {
    const el = $("#status");
    el.textContent = text;
    el.dataset.state = stateName;
  }

  // ---------- Khởi tạo ----------
  function bindUi() {
    document.querySelectorAll(".tab").forEach((tab) => tab.addEventListener("click", () => {
      state.decision = tab.dataset.decision;
      state.page = 1;
      state.selected.clear();
      state.focusId = null;
      reload();
    }));

    const onFilterChange = () => { state.page = 1; state.selected.clear(); reload(); };
    $("#keyword").addEventListener("input", debounce(onFilterChange, 300));
    $("#type").addEventListener("change", onFilterChange);
    $("#reason").addEventListener("change", onFilterChange);
    $("#inMain").addEventListener("change", onFilterChange);
    $("#filters").addEventListener("submit", (e) => e.preventDefault());

    $("#prev").addEventListener("click", () => { state.page--; reload(); });
    $("#next").addEventListener("click", () => { state.page++; reload(); });

    $("#selectPage").addEventListener("change", (e) => {
      for (const item of state.items) {
        if (e.target.checked) state.selected.add(item.id);
        else state.selected.delete(item.id);
      }
      renderCounts();
      renderList();
    });
    $("#clearSel").addEventListener("click", () => { state.selected.clear(); renderCounts(); renderList(); });

    $("#promoteBtn").addEventListener("click", () => act("/api/review/promote", selectedOrFocus(), "Đưa vào CSDL chính"));
    $("#rejectBtn").addEventListener("click", () => act("/api/review/reject", selectedOrFocus(), "Loại"));
    $("#bulkPromote").addEventListener("click", () => bulk("promote"));
    $("#bulkReject").addEventListener("click", () => bulk("reject"));

    $("#list").addEventListener("click", (e) => {
      const restore = e.target.closest('[data-role="restore"]');
      if (restore) {
        act("/api/review/restore", [restore.dataset.id], "Hoàn tác");
        return;
      }
      const row = e.target.closest(".row");
      if (!row) return;
      if (e.target.matches('input[data-role="select"]')) {
        toggleSelect(row.dataset.id);
        return;
      }
      state.focusId = row.dataset.id;
      renderList();
    });

    document.addEventListener("keydown", (e) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.target.matches("input, select, textarea")) return;
      switch (e.key.toLowerCase()) {
        case "j": moveFocusAt(1); break;
        case "k": moveFocusAt(-1); break;
        case " ":
          if (state.focusId) { e.preventDefault(); toggleSelect(state.focusId); }
          break;
        case "p":
          if (state.decision === "pending") act("/api/review/promote", selectedOrFocus(), "Đưa vào CSDL chính");
          break;
        case "r":
          if (state.decision === "pending") act("/api/review/reject", selectedOrFocus(), "Loại");
          break;
      }
    });
  }

  function askName() {
    return new Promise((resolve) => {
      const modal = $("#nameModal");
      const input = $("#nameInput");
      modal.hidden = false;
      input.focus();
      $("#nameForm").addEventListener("submit", (e) => {
        e.preventDefault();
        const name = input.value.trim();
        if (!name) return;
        modal.hidden = true;
        resolve(name);
      }, { once: true });
    });
  }

  async function start() {
    bindUi();
    const name = storage.get() || await askName();
    storage.set(name);
    state.me = { name };

    // Đọc dữ liệu không cần SignalR: tải song song với lúc kết nối
    reload();

    connectHub();
    try {
      await hub.start();
      await joinRoom();
      scheduleReload();   // thay đổi xảy ra trong lúc đang kết nối chưa có sự kiện
    } catch (err) {
      setStatus("Không kết nối được máy chủ", "offline");
      toast(`Lỗi kết nối: ${err.message}`);
    }
  }

  start();
})();
