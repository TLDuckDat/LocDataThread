// Giao diện lọc cộng tác: danh sách dùng chung, cập nhật thời gian thực qua SignalR.
(() => {
  const PAGE_SIZE = 50;
  const NAME_KEY = "threads-toxic-name";
  const HEARTBEAT_MS = 20000;

  const state = {
    me: null,
    bucket: "active",
    page: 1,
    total: 0,
    items: [],
    counts: { active: 0, excluded: 0 },
    selected: new Set(),
    focusId: null,
    locks: {},      // itemId -> tên người đang xem
    presence: [],
    connected: false,
  };

  let hub = null;
  let heartbeat = null;

  const $ = (sel) => document.querySelector(sel);

  // ---------- Tiện ích ----------
  const esc = (value) =>
    String(value ?? "").replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

  const debounce = (fn, ms) => {
    let timer = null;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => fn(...args), ms);
    };
  };

  const colorFor = (name) => {
    let hash = 0;
    for (const ch of name) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
    return `hsl(${hash % 360} 65% 45%)`;
  };

  const scoreClass = (score) => (score >= 0.7 ? "high" : score >= 0.4 ? "mid" : "");

  const formatDate = (raw) => {
    if (!raw) return "—";
    const d = new Date(raw);
    return Number.isNaN(d.getTime()) ? raw : d.toLocaleString("vi-VN");
  };

  const toast = (message) => {
    const el = document.createElement("div");
    el.className = "toast";
    el.textContent = message;
    $("#toasts").append(el);
    setTimeout(() => el.remove(), 3500);
  };

  const storage = {
    get() { try { return localStorage.getItem(NAME_KEY); } catch { return null; } },
    set(v) { try { localStorage.setItem(NAME_KEY, v); } catch { /* chế độ riêng tư: bỏ qua */ } },
  };

  async function api(path, options = {}) {
    const res = await fetch(path, {
      headers: { "Content-Type": "application/json" },
      ...options,
    });
    const body = await res.json().catch(() => null);
    if (!res.ok) throw new Error(typeof body === "string" ? body : "Yêu cầu thất bại.");
    return body;
  }

  // ---------- Tải dữ liệu ----------
  function currentParams() {
    const params = new URLSearchParams({ bucket: state.bucket, page: state.page, pageSize: PAGE_SIZE });
    const map = { keyword: "#keyword", word: "#word", minScore: "#minScore", replyLevel: "#replyLevel" };
    for (const [key, sel] of Object.entries(map)) {
      const value = $(sel).value.trim();
      if (value) params.set(key, value);
    }
    return params;
  }

  async function reload() {
    try {
      const [list, counts] = await Promise.all([
        api(`/api/items?${currentParams()}`),
        api("/api/items/counts"),
      ]);
      state.total = list.total;
      state.items = list.items;
      state.counts = counts;

      const visible = new Set(list.items.map((i) => i.id));
      for (const id of [...state.selected]) if (!visible.has(id)) state.selected.delete(id);
      if (state.focusId && !visible.has(state.focusId)) setFocus(null);

      renderCounts();
      renderList();
      renderDetail();
      renderPager();
    } catch (err) {
      toast(`Không tải được dữ liệu: ${err.message}`);
    }
  }

  const scheduleReload = debounce(reload, 150);

  // ---------- Hiển thị ----------
  function renderCounts() {
    $("#countActive").textContent = state.counts.active.toLocaleString("vi-VN");
    $("#countExcluded").textContent = state.counts.excluded.toLocaleString("vi-VN");
    document.querySelectorAll(".tab").forEach((tab) =>
      tab.classList.toggle("is-active", tab.dataset.bucket === state.bucket));
  }

  function rowHtml(item) {
    const holder = state.locks[item.id];
    const lockedByOther = holder && holder !== state.me?.name;
    const classes = ["row"];
    if (item.id === state.focusId) classes.push("is-focus");
    if (state.selected.has(item.id)) classes.push("is-selected");
    if (lockedByOther) classes.push("is-locked");

    const words = (item.matchedWords || []).slice(0, 6)
      .map((w) => `<span class="tag">${esc(w)}</span>`).join("");
    const lock = lockedByOther ? `<span class="lock">🔒 ${esc(holder)} đang xem</span>` : "";

    return `
      <article class="${classes.join(" ")}" data-id="${esc(item.id)}">
        <label class="check"><input type="checkbox" data-role="select" ${state.selected.has(item.id) ? "checked" : ""} aria-label="Chọn bình luận"></label>
        <div>
          <div class="meta">
            <span class="score ${scoreClass(item.toxicScore)}">${Math.round(item.toxicScore * 100)}%</span>
            <span class="level">F${item.replyLevel}</span>
            <span>@${esc(item.authorUsername || "ẩn danh")}</span>
            ${lock}
          </div>
          <p class="content">${esc(item.content)}</p>
          ${words ? `<div class="tags">${words}</div>` : ""}
        </div>
      </article>`;
  }

  function renderList() {
    const list = $("#list");
    list.innerHTML = state.items.length
      ? state.items.map(rowHtml).join("")
      : `<div class="empty">${state.bucket === "active" ? "Không còn bình luận nào cần lọc với bộ lọc này." : "Chưa có bình luận nào trong mục Đã loại."}</div>`;

    const allChecked = state.items.length > 0 && state.items.every((i) => state.selected.has(i.id));
    $("#selectPage").checked = allChecked;
    $("#selCount").textContent = `Đã chọn ${state.selected.size}`;

    const moveBtn = $("#moveBtn");
    moveBtn.textContent = state.bucket === "active" ? "Loại khỏi danh sách" : "Khôi phục về danh sách";
    moveBtn.disabled = state.items.length === 0;
  }

  function renderPager() {
    const pages = Math.max(1, Math.ceil(state.total / PAGE_SIZE));
    $("#pageInfo").textContent = `Trang ${state.page}/${pages} · ${state.total.toLocaleString("vi-VN")} bình luận`;
    $("#prev").disabled = state.page <= 1;
    $("#next").disabled = state.page >= pages;
  }

  function renderDetail() {
    const panel = $("#detail");
    const item = state.items.find((i) => i.id === state.focusId);
    if (!item) {
      panel.innerHTML = `<div class="empty">Bấm vào một bình luận để xem ngữ cảnh F0 → F3 và các thao tác.</div>`;
      return;
    }

    const levels = [item.f0, item.f1, item.f2, item.f3];
    const chain = levels.slice(0, item.replyLevel + 1).map((text, idx) => {
      const isCurrent = idx === item.replyLevel;
      return `<li class="${isCurrent ? "current" : ""}"><small>F${idx}${isCurrent ? " · bình luận này" : ""}</small>${esc(text || item.content)}</li>`;
    }).join("");

    const holder = state.locks[item.id];
    const lockedByOther = holder && holder !== state.me?.name;
    const categories = (item.categories || []).join(", ") || "—";
    const words = (item.matchedWords || []).join(", ") || "—";
    const moveLabel = item.isExcluded ? "Khôi phục" : "Loại khỏi danh sách";

    panel.innerHTML = `
      <h2>Ngữ cảnh hội thoại</h2>
      <ol class="chain">${chain}</ol>
      <dl class="kv">
        <dt>Tác giả</dt><dd>@${esc(item.authorUsername || "ẩn danh")} ${item.authorName ? `(${esc(item.authorName)})` : ""}</dd>
        <dt>Thời gian</dt><dd>${esc(formatDate(item.postedAt))}</dd>
        <dt>Điểm</dt><dd>${Math.round(item.toxicScore * 100)}% · ${esc(item.severity || "—")}</dd>
        <dt>Phân loại</dt><dd>${esc(categories)}</dd>
        <dt>Từ khớp</dt><dd>${esc(words)}</dd>
        ${item.isExcluded ? `<dt>Đã loại bởi</dt><dd>${esc(item.excludedBy || "—")}</dd>` : ""}
        <dt>Liên kết</dt><dd><a href="${esc(item.commentUrl || item.postUrl)}" target="_blank" rel="noopener">Mở trên Threads ↗</a></dd>
      </dl>
      ${lockedByOther ? `<p class="lock">🔒 ${esc(holder)} đang xem bình luận này</p>` : ""}
      <div class="actions">
        <button class="btn primary" type="button" data-role="move-focus" ${lockedByOther ? "disabled" : ""}>${moveLabel} (X)</button>
      </div>`;
  }

  function renderPresence() {
    const box = $("#presence");
    box.innerHTML = state.presence.map((p) => {
      const focused = p.focusedItemId ? `<span class="focus-dot" title="Đang xem bình luận">●</span>` : "";
      return `<span class="chip"><span class="avatar" style="background:${colorFor(p.name)}">${esc(p.name.charAt(0).toUpperCase())}</span>${esc(p.name)} ${focused}</span>`;
    }).join("");
  }

  function setStatus(text, stateName) {
    const el = $("#status");
    el.textContent = text;
    el.dataset.state = stateName;
  }

  // ---------- Chọn & xem ----------
  function setFocus(id) {
    const previous = state.focusId;
    state.focusId = id;
    if (heartbeat) { clearInterval(heartbeat); heartbeat = null; }

    if (previous && previous !== id && hub && state.connected) {
      hub.invoke("Blur", previous).catch(() => {});
    }
    if (id && hub && state.connected) {
      claimFocus(id);
      heartbeat = setInterval(() => claimFocus(id), HEARTBEAT_MS);
    }
    renderList();
    renderDetail();
  }

  async function claimFocus(id) {
    try {
      const result = await hub.invoke("Focus", id);
      if (!result.granted && state.focusId === id) {
        toast(result.holder
          ? `${result.holder} đang xem bình luận này`
          : "Bạn chưa tham gia phòng, thử tải lại trang.");
      }
    } catch { /* sẽ được làm mới khi kết nối lại */ }
  }

  function toggleSelect(id) {
    if (state.selected.has(id)) state.selected.delete(id);
    else state.selected.add(id);
    renderList();
  }

  function moveFocusAt(offset) {
    if (!state.items.length) return;
    const idx = state.items.findIndex((i) => i.id === state.focusId);
    const next = Math.min(state.items.length - 1, Math.max(0, idx + offset));
    const id = state.items[next].id;
    setFocus(id);
    document.querySelector(`.row[data-id="${CSS.escape(id)}"]`)?.scrollIntoView({ block: "nearest" });
  }

  // ---------- Thao tác chuyển bình luận ----------
  async function moveItems(ids, toExcluded) {
    if (!ids.length) return;

    // Bỏ qua bình luận đang được người khác xem (lease lock)
    const free = ids.filter((id) => !(state.locks[id] && state.locks[id] !== state.me.name));
    const skipped = ids.length - free.length;
    if (!free.length) {
      toast("Các bình luận đã chọn đang được đồng đội xem.");
      return;
    }

    try {
      const result = await api("/api/items/move", {
        method: "POST",
        body: JSON.stringify({ ids: free, toExcluded, userName: state.me.name }),
      });
      const verb = toExcluded ? "Đã loại" : "Đã khôi phục";
      let message = `${verb} ${result.moved} bình luận.`;
      if (skipped) message += ` Bỏ qua ${skipped} bình luận đang được xem.`;
      if (result.skipped) message += ` ${result.skipped} bình luận đã ở đúng mục.`;
      toast(message);
      for (const id of free) state.selected.delete(id);
      if (free.includes(state.focusId)) setFocus(null);
    } catch (err) {
      toast(`Thao tác thất bại: ${err.message}`);
    }
    scheduleReload();
  }

  function moveSelectionOrFocus() {
    const toExcluded = state.bucket === "active";
    const ids = state.selected.size
      ? [...state.selected]
      : (state.focusId ? [state.focusId] : []);
    moveItems(ids, toExcluded);
  }

  // ---------- SignalR ----------
  function connectHub() {
    hub = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/collab")
      .withAutomaticReconnect()
      .build();

    hub.on("ItemsMoved", ({ ids, toExcluded, by }) => {
      for (const id of ids) delete state.locks[id];
      if (by !== state.me.name) {
        toast(`${by} đã ${toExcluded ? "loại" : "khôi phục"} ${ids.length} bình luận.`);
      }
      scheduleReload();
    });

    hub.on("LockChanged", (itemId, holder) => {
      if (holder) state.locks[itemId] = holder;
      else delete state.locks[itemId];
      renderList();
      renderDetail();
    });

    hub.on("LockSnapshot", (locks) => {
      state.locks = {};
      for (const lock of locks) state.locks[lock.itemId] = lock.holder;
      renderList();
      renderDetail();
    });

    hub.on("PresenceChanged", (list) => {
      state.presence = list;
      renderPresence();
    });

    hub.onreconnecting(() => { state.connected = false; setStatus("Đang kết nối lại…", "connecting"); });
    hub.onreconnected(async () => {
      await joinRoom();
      if (state.focusId) claimFocus(state.focusId);
      scheduleReload();
    });
    hub.onclose(() => { state.connected = false; setStatus("Mất kết nối", "offline"); });
  }

  async function joinRoom() {
    await hub.invoke("Join", state.me.name);
    state.connected = true;
    setStatus("Đã kết nối", "online");
  }

  // ---------- Khởi tạo ----------
  function bindUi() {
    document.querySelectorAll(".tab").forEach((tab) => tab.addEventListener("click", () => {
      state.bucket = tab.dataset.bucket;
      state.page = 1;
      state.selected.clear();
      setFocus(null);
      reload();
    }));

    const onFilterChange = () => { state.page = 1; reload(); };
    $("#keyword").addEventListener("input", debounce(onFilterChange, 300));
    $("#word").addEventListener("input", debounce(onFilterChange, 300));
    $("#minScore").addEventListener("change", onFilterChange);
    $("#replyLevel").addEventListener("change", onFilterChange);
    $("#filters").addEventListener("submit", (e) => e.preventDefault());

    $("#prev").addEventListener("click", () => { state.page--; reload(); });
    $("#next").addEventListener("click", () => { state.page++; reload(); });

    $("#selectPage").addEventListener("change", (e) => {
      for (const item of state.items) {
        if (e.target.checked) state.selected.add(item.id);
        else state.selected.delete(item.id);
      }
      renderList();
    });
    $("#clearSel").addEventListener("click", () => { state.selected.clear(); renderList(); });
    $("#moveBtn").addEventListener("click", moveSelectionOrFocus);

    $("#list").addEventListener("click", (e) => {
      const row = e.target.closest(".row");
      if (!row) return;
      const id = row.dataset.id;
      if (e.target.matches('input[data-role="select"]')) {
        toggleSelect(id);
        return;
      }
      setFocus(id);
    });

    $("#detail").addEventListener("click", (e) => {
      if (e.target.matches('[data-role="move-focus"]')) {
        const item = state.items.find((i) => i.id === state.focusId);
        if (item) moveItems([item.id], !item.isExcluded);
      }
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
        case "x":
          if (state.focusId || state.selected.size) moveSelectionOrFocus();
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

    connectHub();
    try {
      await hub.start();
      await joinRoom();
    } catch (err) {
      setStatus("Không kết nối được máy chủ", "offline");
      toast(`Lỗi kết nối: ${err.message}`);
    }
    reload();
  }

  start();
})();
