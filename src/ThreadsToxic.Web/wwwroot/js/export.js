// Trang xuất Excel/CSV: giống trang "📥 Xuất dữ liệu Excel & CSV" của crawler (bảng 4 cột chuẩn).
(() => {
  const { esc } = window.ThreadsUtil;
  const $ = (id) => document.getElementById(id);

  const PRESETS = {
    six: ["Bài viết", "f0", "f1", "f2", "f3", "Nội dung"],
    tree: ["Bài viết", "f0", "f1", "f2", "f3", "Nội dung", "Điểm đánh giá", "Chủ đề bài viết"],
    core: ["Bài viết", "Nội dung", "Điểm đánh giá", "Chủ đề bài viết"],
    review: ["Bài viết", "f0", "f1", "f2", "f3", "Nội dung", "Điểm đánh giá", "Chủ đề bài viết",
      "Tự đánh giá của bạn (Xấu luôn / Chưa rõ / Trong sạch)"],
  };
  // Bên crawler: ẩn hết cột thì vẫn xem và xuất 2 cột này
  const FALLBACK_COLUMNS = ["Bài viết", "Nội dung"];

  const state = {
    allColumns: [],
    rows: [],
    chosen: null,          // null = chưa tải lần nào: mặc định chọn PRESETS.six
    previewLimit: 60,
    files: {},             // kind -> { signature, url, name }
    requestId: 0,
  };

  function options() {
    const form = $("exportForm");
    return {
      status: form.elements.status.value,
      type: form.elements.type.value,
      minLength: clampInput($("minLength")),
      maxLength: clampInput($("maxLength")),
      dedup: $("dedup").checked,
      reviewed: $("reviewed").checked,
    };
  }

  // Ô số giữ trong khoảng cho phép (như number_input của crawler)
  function clampInput(input) {
    const min = Number(input.min), max = Number(input.max);
    let value = Math.trunc(Number(input.value));
    if (!Number.isFinite(value)) value = Number(input.defaultValue);
    value = Math.min(max, Math.max(min, value));
    if (String(value) !== input.value) input.value = String(value);
    return value;
  }

  function effectiveColumns() {
    const chosen = state.chosen ?? state.allColumns;
    return chosen.length ? chosen : FALLBACK_COLUMNS;
  }

  function query(withColumns) {
    const o = options();
    const params = new URLSearchParams({
      status: o.status, type: o.type,
      minLength: String(o.minLength), maxLength: String(o.maxLength), dedup: String(o.dedup),
      reviewed: String(o.reviewed),
    });
    if (withColumns) effectiveColumns().forEach((c) => params.append("columns", c));
    return params;
  }

  // File đã tạo chỉ còn hiệu lực khi bộ lọc và danh sách cột không đổi
  function signature() {
    return query(true).toString();
  }

  async function loadPreview() {
    const id = ++state.requestId;
    $("previewLoading").hidden = false;
    renderDownloads();     // file đã tạo theo bộ lọc cũ không còn đúng: ẩn ngay

    try {
      const res = await fetch(`/api/export/curated?${query(false)}`);
      if (!res.ok) throw new Error(await res.text() || res.statusText);
      const data = await res.json();
      if (id !== state.requestId) return;
      state.allColumns = data.columns;
      state.rows = data.rows;
      state.previewLimit = data.previewLimit;
      // Mặc định: đúng 6 cột (Bài viết, f0–f3, Nội dung); bỏ chọn/chọn lại tự do sau đó
      if (state.chosen === null) state.chosen = PRESETS.six.filter((c) => data.columns.includes(c));
      render();
    } catch (err) {
      if (id === state.requestId) toast(`Không tải được dữ liệu xem trước: ${err.message}`);
    } finally {
      if (id === state.requestId) $("previewLoading").hidden = true;
    }
  }

  function render() {
    const empty = state.rows.length === 0;
    $("previewEmpty").hidden = !empty;
    $("previewBody").hidden = empty;
    if (empty) return;

    renderColumnPicker();
    const columns = effectiveColumns();
    $("noColumnsWarning").hidden = state.chosen.length > 0;
    $("previewInfo").innerHTML =
      `Đang xem trước tối đa <b>${state.previewLimit} dòng</b> trong mẫu (${state.rows.length} dòng). ` +
      `File xuất sẽ gồm toàn bộ dữ liệu phù hợp, với <b>${columns.length}</b> cột đã chọn.`;
    renderTable(columns);
    renderDownloads();
  }

  function renderColumnPicker() {
    $("chosenColumns").innerHTML = state.chosen.map((c, i) =>
      `<span class="chip col-chip">${esc(c)}<button type="button" class="chip-x" data-remove="${i}" aria-label="Bỏ cột ${esc(c)}">×</button></span>`
    ).join("");
    const remaining = state.allColumns.filter((c) => !state.chosen.includes(c));
    $("addColumn").innerHTML = `<option value="">${remaining.length ? "+ Thêm cột…" : "Đã chọn tất cả cột"}</option>` +
      remaining.map((c) => `<option value="${esc(c)}">${esc(c)}</option>`).join("");
    $("addColumn").disabled = remaining.length === 0;
  }

  function renderTable(columns) {
    const index = columns.map((c) => state.allColumns.indexOf(c));
    const head = `<thead><tr><th class="row-no"></th>${columns.map((c) => `<th>${esc(c)}</th>`).join("")}</tr></thead>`;
    const body = state.rows.map((row, r) =>
      `<tr><td class="row-no">${r}</td>${index.map((i) => `<td>${esc(row[i])}</td>`).join("")}</tr>`
    ).join("");
    $("previewTable").innerHTML = `${head}<tbody>${body}</tbody>`;
  }

  function renderDownloads() {
    const count = effectiveColumns().length;
    const current = signature();
    const labels = {
      xlsx: `📊 TẢI EXCEL (.XLSX) CHỨA ${count} CỘT ĐÃ CHỌN`,
      csv: `📄 TẢI CSV CHỨA ${count} CỘT ĐÃ CHỌN`,
    };
    for (const kind of ["xlsx", "csv"]) {
      const link = $(kind === "xlsx" ? "getXlsx" : "getCsv");
      const caption = $(kind === "xlsx" ? "xlsxCaption" : "csvCaption");
      const file = state.files[kind];
      const valid = file && file.signature === current;
      link.hidden = caption.hidden = !valid;
      if (!valid) continue;
      link.href = file.url;
      link.download = file.name;
      link.textContent = labels[kind];
      if (kind === "xlsx") caption.innerHTML = `✅ Đã fix size ô, wrap text và <b>chỉ xuất ${count} cột</b>.`;
    }
  }

  async function makeFile(kind) {
    const button = $(kind === "xlsx" ? "makeXlsx" : "makeCsv");
    const label = button.textContent;
    const what = kind === "xlsx" ? "file Excel" : "file CSV";
    const sig = signature();
    button.disabled = true;
    // Server tải dữ liệu theo lô rồi dựng file: hiện thời gian đã chờ, sau đó % tải về
    const started = Date.now();
    let phase = `Đang tạo ${what} từ toàn bộ dữ liệu phù hợp`;
    const tick = () => { button.textContent = `${phase}… ${Math.round((Date.now() - started) / 1000)} giây`; };
    tick();
    const timer = setInterval(tick, 1000);
    try {
      const res = await fetch(`/api/export/curated/${kind}?${query(true)}`);
      if (!res.ok) throw new Error(await res.text() || res.statusText);
      const blob = await readWithProgress(res, (percent) => {
        phase = `Đang tải ${what} về ${percent}%`;
        tick();
      });
      const old = state.files[kind];
      if (old) URL.revokeObjectURL(old.url);
      state.files[kind] = { signature: sig, url: URL.createObjectURL(blob), name: fileName(res, kind) };
      renderDownloads();
    } catch (err) {
      toast(`Không thể tạo ${what}: ${err.message}`);
    } finally {
      clearInterval(timer);
      button.disabled = false;
      button.textContent = label;
    }
  }

  // Đọc phản hồi từng phần để báo % tải về (server gửi Content-Length); không có thì đọc một lần
  async function readWithProgress(res, onProgress) {
    const total = Number(res.headers.get("Content-Length")) || 0;
    if (!res.body || !total) return res.blob();
    const reader = res.body.getReader();
    const chunks = [];
    let received = 0;
    onProgress(0);
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      received += value.length;
      onProgress(Math.min(100, Math.floor((received * 100) / total)));
    }
    return new Blob(chunks, { type: res.headers.get("Content-Type") || "" });
  }

  function fileName(res, kind) {
    const header = res.headers.get("Content-Disposition") || "";
    const encoded = /filename\*=UTF-8''([^;]+)/i.exec(header);
    if (encoded) return decodeURIComponent(encoded[1]);
    const plain = /filename="?([^";]+)"?/i.exec(header);
    return plain ? plain[1] : `threads_danh_gia_4_cot.${kind}`;
  }

  function toast(text) {
    const el = document.createElement("div");
    el.className = "toast";
    el.textContent = text;
    $("toasts").append(el);
    setTimeout(() => el.remove(), 6000);
  }

  function setChosen(columns) {
    state.chosen = columns.filter((c) => state.allColumns.includes(c));
    render();
  }

  let debounce;
  $("exportForm").addEventListener("change", () => loadPreview());
  $("exportForm").addEventListener("input", (e) => {
    if (e.target.type !== "number") return;
    clearTimeout(debounce);
    debounce = setTimeout(loadPreview, 400);
  });
  $("exportForm").addEventListener("submit", (e) => e.preventDefault());

  document.querySelector(".presets").addEventListener("click", (e) => {
    const preset = e.target.closest("[data-preset]")?.dataset.preset;
    if (!preset) return;
    setChosen(preset === "all" ? [...state.allColumns] : PRESETS[preset]);
  });

  $("chosenColumns").addEventListener("click", (e) => {
    const remove = e.target.closest("[data-remove]")?.dataset.remove;
    if (remove === undefined) return;
    setChosen(state.chosen.filter((_, i) => i !== Number(remove)));
  });

  // Cột thêm vào nằm cuối danh sách: thứ tự cột trong file đúng thứ tự đã chọn
  $("addColumn").addEventListener("change", (e) => {
    if (e.target.value) setChosen([...state.chosen, e.target.value]);
  });

  $("makeXlsx").addEventListener("click", () => makeFile("xlsx"));
  $("makeCsv").addEventListener("click", () => makeFile("csv"));

  loadPreview();
})();
