// Giao diện lọc cộng tác: danh sách dùng chung, cập nhật thời gian thực qua SignalR.
(() => {
  const PAGE_SIZE = 50;

  // Lý do bị cách ly ở hàng chờ lọc trùng (cột source); null = dữ liệu chính
  const SOURCE_LABELS = {
    duplicate_content: "Trùng nội dung",
    duplicate_id: "Trùng id",
    non_vietnamese: "Không phải tiếng Việt",
    meta_ai: "Meta AI",
  };
  const sourceLabel = (source) => SOURCE_LABELS[source] || source;
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
    token: null,    // token phiên do server cấp khi Join (gửi trong header X-Session-Token)
    locks: {},      // itemId -> tên người đang xem
    presence: [],
    connected: false,
  };

  let hub = null;
  let heartbeat = null;

  const $ = (sel) => document.querySelector(sel);

  // ---------- Tiện ích ----------
  // Mọi dữ liệu từ server/người dùng chèn vào HTML phải đi qua esc(); URL phải qua safeUrl().
  const { esc, safeUrl, toInt, createPageCache } = window.ThreadsUtil;

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

  // options: { actions: [{ label, onClick }], ms }. Nút hành động (vd. "Hoàn tác", "Vẫn chuyển") là tùy chọn.
  const toast = (message, options = {}) => {
    const el = document.createElement("div");
    el.className = "toast";

    const text = document.createElement("span");
    text.textContent = message;
    el.append(text);

    for (const action of options.actions || []) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "toast-action";
      button.textContent = action.label;
      button.addEventListener("click", () => {
        el.remove();
        action.onClick();
      });
      el.append(button);
    }

    $("#toasts").append(el);
    setTimeout(() => el.remove(), options.ms ?? 3500);
  };

  async function api(path, options = {}) {
    const res = await fetch(path, {
      ...options,
      headers: ThreadsAuth.headers({ "Content-Type": "application/json", ...(options.headers || {}) }),
    });
    const body = await res.json().catch(() => null);
    // Đọc dữ liệu bị từ chối: mã truy cập đã bị admin đổi hoặc gỡ -> nhập lại mã
    if (res.status === 401 && !options.method) ThreadsAuth.signOut();
    if (!res.ok) {
      const error = new Error(typeof body === "string" ? body : "Yêu cầu thất bại.");
      error.status = res.status;
      throw error;
    }
    return body;
  }

  // Gọi API ghi bằng token phiên do server cấp. Token hết hạn (401, ví dụ sau khi kết nối lại)
  // thì Join lại để lấy token mới và thử lại đúng một lần.
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

  // ---------- Tải dữ liệu ----------
  function currentParams() {
    const params = new URLSearchParams({ bucket: state.bucket, page: state.page, pageSize: PAGE_SIZE });
    const map = {
      keyword: "#keyword", word: "#word", minScore: "#minScore", replyLevel: "#replyLevel", source: "#source",
      review: "#review", reviewedBy: "#reviewedBy", assigned: "#assigned", order: "#order",
    };
    for (const [key, sel] of Object.entries(map)) {
      const value = $(sel).value.trim();
      if (value) params.set(key, value);
    }
    return params;
  }

  // Neo vị trí nhìn vào bình luận đang focus (hoặc dòng đầu tiên đang hiện) để danh sách
  // tải lại không làm màn hình nhảy.
  function captureViewAnchor() {
    const rows = [...document.querySelectorAll("#list .row")];
    const inView = (el) => {
      const rect = el.getBoundingClientRect();
      return rect.bottom > 0 && rect.top < window.innerHeight;
    };
    const target = rows.find((r) => r.dataset.id === state.focusId && inView(r)) || rows.find(inView);
    return target ? { id: target.dataset.id, top: target.getBoundingClientRect().top } : null;
  }

  function restoreViewAnchor(anchor) {
    if (!anchor) return;
    const row = document.querySelector(`#list .row[data-id="${CSS.escape(anchor.id)}"]`);
    if (row) window.scrollBy(0, row.getBoundingClientRect().top - anchor.top);
  }

  // Mỗi lần tải được đánh số; phản hồi về muộn của lần tải cũ (vd. trước khi đổi bộ lọc,
  // hoặc do đồng đội vừa chuyển bình luận) bị bỏ qua để không đè lên kết quả mới hơn.
  let reloadSeq = 0;

  // Trang kế tiếp được tải trước ở nền nên bấm "Sau →" hiện ngay, không chờ một lượt đi về server.
  const pageCache = createPageCache();
  const fetchPage = (query) => api(`/api/items?${query}`);

  function prefetchNextPage() {
    if (state.page >= Math.ceil(state.total / PAGE_SIZE)) return;
    const next = currentParams();
    next.set("page", String(state.page + 1));
    pageCache.prefetch(next.toString(), fetchPage);
  }

  // Danh sách cũ vẫn hiện (mờ đi) trong lúc tải; chỉ làm mờ khi chờ quá 150 ms để không nháy.
  function setLoading(on) {
    $("#list").classList.toggle("is-loading", on);
    $("#list").setAttribute("aria-busy", String(on));
  }

  async function reload() {
    const seq = ++reloadSeq;
    const loadingTimer = setTimeout(() => { if (seq === reloadSeq) setLoading(true); }, 150);
    try {
      const list = await pageCache.get(currentParams().toString(), fetchPage);
      // Phản hồi mới đã kèm counts; gọi /counts chỉ để tương thích với server cũ.
      const counts = list.counts ?? await api("/api/items/counts");
      if (seq !== reloadSeq) return;

      const anchor = captureViewAnchor();
      const detailScroll = $("#detail").scrollTop;

      state.total = list.total;
      state.items = list.items;
      state.counts = counts;

      const visible = new Set(list.items.map((i) => i.id));
      for (const id of [...state.selected]) if (!visible.has(id)) state.selected.delete(id);
      // Bình luận đang focus biến mất (người khác đã chuyển): bỏ focus thay vì nhảy sang dòng khác,
      // để phím X không tác động nhầm lên một bình luận mà người dùng chưa kịp đọc.
      if (state.focusId && !visible.has(state.focusId)) setFocus(null);

      renderCounts();
      renderList();
      renderDetail();
      renderPager();

      restoreViewAnchor(anchor);
      $("#detail").scrollTop = detailScroll;
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

  // Dữ liệu vừa đổi (thao tác của mình hoặc đồng đội): bỏ các trang tải trước rồi tải lại.
  function scheduleReload() {
    pageCache.clear();
    debouncedReload();
  }

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
    const badge = reviewBadge(item);

    return `
      <article class="${classes.join(" ")}" data-id="${esc(item.id)}">
        <label class="check"><input type="checkbox" data-role="select" ${state.selected.has(item.id) ? "checked" : ""} aria-label="Chọn bình luận"></label>
        <div>
          <div class="meta">
            <span class="score ${scoreClass(item.toxicScore)}">${Math.round(item.toxicScore * 100)}%</span>
            <span class="level">F${toInt(item.replyLevel)}</span>
            ${item.source ? `<span class="source" title="Nạp từ hàng chờ lọc trùng">Lọc trùng · ${esc(sourceLabel(item.source))}</span>` : ""}
            <span>@${esc(item.authorUsername || "ẩn danh")}</span>
            ${badge}
            ${lock}
          </div>
          <p class="content">${esc(item.content)}</p>
          ${words ? `<div class="tags">${words}</div>` : ""}
        </div>
      </article>`;
  }

  // Người xử lý gần nhất (duyệt, loại, khôi phục); chỉ hiện nếu đã có người xử lý
  function reviewBadge(item) {
    if (!item.reviewedBy) return "";
    const when = item.reviewedAt ? ` · ${formatDate(item.reviewedAt)}` : "";
    return `<span class="reviewed" title="Người xử lý gần nhất${esc(when)}">✓ ${esc(item.reviewedBy)}</span>`;
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

    const level = Math.min(3, toInt(item.replyLevel));
    const levels = [item.f0, item.f1, item.f2, item.f3];
    const chain = levels.slice(0, level + 1).map((text, idx) => {
      const isCurrent = idx === level;
      return `<li class="${isCurrent ? "current" : ""}"><small>F${idx}${isCurrent ? " · bình luận này" : ""}</small>${esc(text || item.content)}</li>`;
    }).join("");

    const holder = state.locks[item.id];
    const lockedByOther = holder && holder !== state.me?.name;
    const categories = (item.categories || []).join(", ") || "—";
    const words = (item.matchedWords || []).join(", ") || "—";
    const moveLabel = item.isExcluded ? "Khôi phục" : "Loại khỏi danh sách";
    const approveBtn = item.isExcluded ? "" :
      `<button class="btn" type="button" data-role="approve-focus">Giữ lại ✓ (A)</button>`;
    // Chỉ http(s): chặn javascript:, data:... có thể nằm trong dữ liệu cào về.
    const href = safeUrl(item.commentUrl) || safeUrl(item.postUrl);
    const link = href
      ? `<a href="${esc(href)}" target="_blank" rel="noopener noreferrer">Mở trên Threads ↗</a>`
      : "—";

    panel.innerHTML = `
      <h2>Ngữ cảnh hội thoại</h2>
      <ol class="chain">${chain}</ol>
      <dl class="kv">
        <dt>Tác giả</dt><dd>@${esc(item.authorUsername || "ẩn danh")} ${item.authorName ? `(${esc(item.authorName)})` : ""}</dd>
        <dt>Thời gian</dt><dd>${esc(formatDate(item.postedAt))}</dd>
        <dt>Điểm</dt><dd>${Math.round(item.toxicScore * 100)}% · ${esc(item.severity || "—")}</dd>
        <dt>Phân loại</dt><dd>${esc(categories)}</dd>
        <dt>Từ khớp</dt><dd>${esc(words)}</dd>
        <dt>Nguồn</dt><dd>${item.source ? `Lọc trùng · ${esc(sourceLabel(item.source))}` : "Dữ liệu chính"}</dd>
        ${item.isExcluded ? `<dt>Đã loại bởi</dt><dd>${esc(item.excludedBy || "—")}</dd>` : ""}
        ${item.isExcluded && item.threadParentId ? `<dt>Khôi phục</dt><dd>Cần bình luận cha đang ở "Đang lọc"; nếu không, dùng "Khôi phục cả chuỗi"</dd>` : ""}
        ${!item.isExcluded && item.replyLevel < 3 ? `<dt>Lưu ý</dt><dd>Loại bình luận này sẽ loại kèm mọi trả lời bên dưới</dd>` : ""}
        <dt>Xử lý gần nhất</dt><dd>${item.reviewedBy ? `${esc(item.reviewedBy)} · ${esc(formatDate(item.reviewedAt))}` : "Chưa ai xử lý"}</dd>
        <dt>Liên kết</dt><dd>${link}</dd>
      </dl>
      ${lockedByOther ? `<p class="lock">🔒 ${esc(holder)} đang xem bình luận này</p>` : ""}
      <div class="actions">
        <button class="btn primary" type="button" data-role="move-focus" ${lockedByOther ? "disabled" : ""}>${moveLabel} (X)</button>
        ${approveBtn}
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
  const UNDO_WINDOW_MS = 8000;

  // Server quyết định bình luận nào bị khóa bởi người khác (trả về trong result.locked).
  // Chuỗi hội thoại: loại một bình luận thì các trả lời bên dưới bị loại theo (result.cascaded);
  // khôi phục một trả lời khi cha/ông còn ở "Đã loại" bị chặn (result.blocked), trừ khi withAncestors.
  async function moveItems(ids, toExcluded, { force = false, withAncestors = false } = {}) {
    if (!ids.length) return;

    try {
      const result = await postWithSession("/api/items/move", { ids, toExcluded, force, withAncestors });
      const locked = result.locked || [];
      const blocked = result.blocked || [];
      const movedIds = result.movedIds || [];
      const handled = ids.filter((id) => !locked.includes(id) && !blocked.includes(id));

      const verb = toExcluded ? "Đã loại" : "Đã khôi phục";
      let message = `${verb} ${result.moved} bình luận.`;
      if (result.cascaded?.length) message += ` Kèm ${result.cascaded.length} trả lời bên dưới (loại theo cha).`;
      if (result.restoredAncestors?.length) message += ` Kèm ${result.restoredAncestors.length} bình luận cha để chuỗi liền mạch.`;
      if (result.skipped) message += ` ${result.skipped} bình luận đã ở đúng mục.`;

      const actions = [];
      if (movedIds.length) {
        // Hoàn tác chỉ đụng tới các id đã đổi thật, không phải toàn bộ lô đã chọn.
        actions.push({ label: "Hoàn tác", onClick: () => undoItems(movedIds, !toExcluded) });
      }
      if (locked.length) {
        const holders = [...new Set(locked.map((id) => state.locks[id]).filter(Boolean))];
        message += ` Bỏ qua ${locked.length} bình luận (hoặc trả lời bên dưới) đang được ${holders.join(", ") || "đồng đội"} xem.`;
        // "Vẫn chuyển" (vượt khóa) chỉ dành cho admin
        if (state.me?.role === "admin") {
          actions.push({ label: "Vẫn chuyển", onClick: () => moveItems(locked, toExcluded, { force: true }) });
        }
      }
      if (blocked.length) {
        message += ` ${blocked.length} bình luận chưa khôi phục được vì bình luận cha vẫn ở "Đã loại".`;
        actions.push({ label: "Khôi phục cả chuỗi", onClick: () => moveItems(blocked, false, { withAncestors: true }) });
      }
      toast(message, { actions, ms: actions.length ? UNDO_WINDOW_MS : undefined });

      for (const id of handled) state.selected.delete(id);
      if (handled.includes(state.focusId)) setFocus(nextFocusAfter(new Set([...handled, ...movedIds])));
    } catch (err) {
      toast(`Thao tác thất bại: ${err.message}`);
    }
    scheduleReload();
  }

  // Hoàn tác thao tác của chính mình. Bình luận đã bị người khác thao tác sau đó được giữ nguyên.
  async function undoItems(ids, toExcluded) {
    try {
      const result = await postWithSession("/api/items/undo", { ids, toExcluded });
      let message = `Đã hoàn tác ${result.undone} bình luận.`;
      if (result.cascaded?.length) message += ` Kèm ${result.cascaded.length} trả lời đã bị kéo theo.`;
      if (result.conflicts.length) {
        message += ` ${result.conflicts.length} bình luận đã bị người khác thao tác sau đó nên giữ nguyên.`;
      }
      if (result.locked.length) {
        message += ` ${result.locked.length} bình luận đang được đồng đội xem nên bỏ qua.`;
      }
      toast(message, { ms: result.conflicts.length || result.locked.length ? UNDO_WINDOW_MS : undefined });
    } catch (err) {
      toast(`Không hoàn tác được: ${err.message}`);
    }
    scheduleReload();
  }

  // Giữ lại: đánh dấu đã duyệt, bình luận vẫn ở "Đang lọc" (không khóa, không thể hoàn tác bằng nút này)
  // Bình luận kế tiếp để xem sau khi xử lý xong bình luận đang xem (bỏ qua những dòng vừa đổi)
  function nextFocusAfter(done) {
    const idx = state.items.findIndex((i) => i.id === state.focusId);
    const after = state.items.slice(idx + 1).find((i) => !done.has(i.id));
    const before = state.items.slice(0, Math.max(idx, 0)).reverse().find((i) => !done.has(i.id));
    return (after || before)?.id ?? null;
  }

  async function approveItems(ids) {
    if (!ids.length) return;
    try {
      const result = await postWithSession("/api/items/approve", { ids });
      if (ids.includes(state.focusId)) setFocus(nextFocusAfter(new Set(ids)));
      toast(`Đã giữ lại ${result.approved} bình luận.${result.skipped ? ` ${result.skipped} bình luận đã được loại nên bỏ qua.` : ""}`);
      for (const id of ids) state.selected.delete(id);
    } catch (err) {
      toast(`Không giữ lại được: ${err.message}`);
    }
    refreshProgress();
    scheduleReload();
  }

  function approveSelectionOrFocus() {
    const ids = state.selected.size
      ? [...state.selected]
      : (state.focusId ? [state.focusId] : []);
    approveItems(ids);
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

    hub.on("ItemsMoved", ({ ids, toExcluded, by, counts }) => {
      for (const id of ids) delete state.locks[id];
      if (by !== state.me?.name) {
        toast(`${by} đã ${toExcluded ? "loại" : "khôi phục"} ${ids.length} bình luận.`);
      }
      // Cập nhật huy hiệu ngay; thiếu trường (server cũ) thì chỉ dựa vào lần tải lại.
      if (counts) {
        state.counts = counts;
        renderCounts();
      }
      refreshProgress();
      scheduleReload();
    });

    hub.on("ItemsReviewed", ({ by, ids }) => {
      if (by !== state.me?.name) toast(`${by} đã giữ lại ${ids.length} bình luận.`);
      refreshProgress();
      scheduleReload();
    });

    hub.on("AssignmentsChanged", () => {
      refreshProgress();
      scheduleReload();
      if (!$("#assignModal").hidden) loadAssignments();
    });

    hub.on("QueueChanged", () => {
      refreshQueueCount();
      scheduleReload();     // bản ghi vừa đưa vào CSDL chính hiện ngay trong "Đang lọc"
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

    // Token gắn với connection id cũ nên mất hiệu lực ngay khi kết nối đứt.
    hub.onreconnecting(() => {
      state.connected = false;
      state.token = null;
      setStatus("Đang kết nối lại…", "connecting");
    });
    hub.onreconnected(async () => {
      await joinRoom();   // lấy token mới
      if (state.focusId) claimFocus(state.focusId);
      scheduleReload();
    });
    hub.onclose(() => {
      state.connected = false;
      state.token = null;
      setStatus("Mất kết nối", "offline");
    });
  }

  // Vào phòng bằng mã truy cập; tên và vai trò do server trả về theo mã. Mã sai thì hỏi lại.
  async function joinRoom() {
    for (;;) {
      try {
        const result = await hub.invoke("Join", await ThreadsAuth.ensure());
        state.me = { name: result.name, role: result.role };
        state.token = result.token;
        state.connected = true;
        setStatus("Đã kết nối", "online");
        renderMe();
        return;
      } catch (err) {
        if (!String(err.message).includes("Mã truy cập không đúng")) throw err;
        ThreadsAuth.clear();
        await ThreadsAuth.ask("Mã truy cập không đúng hoặc đã bị đổi. Nhập lại mã do admin cấp.");
      }
    }
  }

  // ---------- Chia việc (admin) ----------
  async function loadAssignments() {
    try {
      const a = await api("/api/items/assignments");
      $("#assignRows").innerHTML = a.people.map((p) =>
        `<tr><td>${esc(p.name)}</td><td>${p.assigned.toLocaleString("vi-VN")}</td>` +
        `<td>${p.done.toLocaleString("vi-VN")}</td><td><b>${p.remaining.toLocaleString("vi-VN")}</b></td></tr>`).join("");
      $("#assignUnassigned").textContent =
        `Chưa giao (chưa xử lý): ${a.unassignedRemaining.toLocaleString("vi-VN")} · ` +
        `bình luận dài không có trả lời (không giao): ${a.longLeaves.toLocaleString("vi-VN")}`;
      const options = a.people.map((p) => `<option value="${esc(p.name)}">${esc(p.name)}</option>`).join("");
      $("#transferFrom").innerHTML = options;
      $("#transferTo").innerHTML = options;
    } catch (err) {
      toast(`Không tải được phần việc: ${err.message}`);
    }
  }

  async function splitWork() {
    if (!confirm("Chia đều toàn bộ phần chưa giao cho các thành viên?")) return;
    try {
      await postWithSession("/api/admin/assign/split", {});
      toast("Đã chia việc.");
      loadAssignments();
    } catch (err) {
      toast(`Không chia được: ${err.message}`);
    }
  }

  async function transferWork() {
    const from = $("#transferFrom").value;
    const to = $("#transferTo").value;
    if (from === to) { toast("Chọn hai người khác nhau."); return; }
    if (!confirm(`Chuyển toàn bộ phần chưa làm của ${from} sang ${to}?`)) return;
    try {
      const result = await postWithSession("/api/admin/assign/transfer", { from, to });
      toast(`Đã chuyển ${result.moved} bình luận từ ${from} sang ${to}.`);
      loadAssignments();
    } catch (err) {
      toast(`Không chuyển được: ${err.message}`);
    }
  }

  // Lần đầu vào: đã được giao việc thì mở "Phần của tôi · chưa xử lý", chưa thì xem tất cả
  async function chooseDefaultView() {
    try {
      const a = await api("/api/items/assignments");
      const mine = a.people.find((x) => x.name === state.me?.name);
      if (mine?.assigned) {
        $("#assigned").value = "me";
        $("#review").value = "unreviewed";
      } else {
        $("#assigned").value = "";
      }
    } catch {
      $("#assigned").value = "";
    }
  }

  function renderMe() {
    $("#assignBtn").hidden = state.me?.role !== "admin";
    $("#me").textContent = state.me ? `${state.me.name}${state.me.role === "admin" ? " · admin" : ""}` : "";
  }

  // ---------- Tiến độ & Kho phụ ----------
  async function refreshProgress() {
    try {
      const [p, a] = await Promise.all([api("/api/items/progress"), api("/api/items/assignments")]);
      const mine = a.people.find((x) => x.name === state.me?.name);
      $("#myProgress").textContent = mine && mine.assigned
        ? `Phần của tôi: còn ${mine.remaining.toLocaleString("vi-VN")}/${mine.assigned.toLocaleString("vi-VN")}`
        : "";
      const today = p.todayByReviewer.map((r) => `${esc(r.name)} ${r.count}`).join(" · ") || "chưa có";
      const top = p.byReviewer.slice(0, 5).map((r) => `${esc(r.name)} ${r.count}`).join(" · ") || "chưa có";
      $("#progress").innerHTML =
        `Đã xử lý <b>${p.reviewed.toLocaleString("vi-VN")}</b>/${(p.active + p.excluded).toLocaleString("vi-VN")}` +
        ` · hôm nay: ${today}` +
        ` · tổng: ${top}`;
    } catch { /* không có tiến độ thì bỏ qua, không chặn thao tác */ }
  }

  async function refreshQueueCount() {
    try {
      const counts = await api("/api/review/counts");
      $("#queueCount").textContent = counts.pending.toLocaleString("vi-VN");
    } catch { /* bỏ qua */ }
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
    $("#source").addEventListener("change", onFilterChange);
    $("#review").addEventListener("change", onFilterChange);
    $("#reviewedBy").addEventListener("input", debounce(onFilterChange, 300));
    $("#assigned").addEventListener("change", onFilterChange);
    $("#order").addEventListener("change", onFilterChange);
    $("#assignBtn").addEventListener("click", () => { $("#assignModal").hidden = false; loadAssignments(); });
    $("#assignClose").addEventListener("click", () => { $("#assignModal").hidden = true; });
    $("#splitBtn").addEventListener("click", splitWork);
    $("#transferBtn").addEventListener("click", transferWork);
    $("#approveBtn").addEventListener("click", approveSelectionOrFocus);
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
      if (e.target.matches('[data-role="approve-focus"]') && state.focusId) {
        approveItems([state.focusId]);
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
        case "a":
          if (state.bucket === "active" && (state.focusId || state.selected.size)) approveSelectionOrFocus();
          break;
      }
    });
  }

  async function start() {
    bindUi();
    $("#signOut").addEventListener("click", () => ThreadsAuth.signOut());
    await ThreadsAuth.ensure();

    connectHub();
    try {
      await hub.start();
      await joinRoom();
    } catch (err) {
      setStatus("Không kết nối được máy chủ", "offline");
      toast(`Lỗi kết nối: ${err.message}`);
    }
    await chooseDefaultView();
    reload();
    refreshProgress();
    refreshQueueCount();
  }

  start();
})();
