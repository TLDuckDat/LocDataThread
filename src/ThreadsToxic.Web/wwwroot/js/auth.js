// Mã truy cập vĩnh viễn (dùng chung cho mọi trang): hỏi một lần, trình duyệt nhớ trong localStorage.
// Mã là bí mật: chỉ gửi trong header X-Access-Token (đọc dữ liệu) và lúc Join hub, không đưa vào URL.
(() => {
  const KEY = "threads-toxic-access";
  let memory = null;   // dự phòng khi trình duyệt chặn localStorage (chế độ riêng tư)

  const get = () => {
    try { return localStorage.getItem(KEY) || memory; } catch { return memory; }
  };
  const set = (value) => {
    memory = value;
    try { localStorage.setItem(KEY, value); } catch { /* chỉ nhớ trong tab này */ }
  };
  const clear = () => {
    memory = null;
    try { localStorage.removeItem(KEY); } catch { /* bỏ qua */ }
  };

  // Hộp nhập mã; dựng bằng DOM (không chèn HTML thô) nên không cần esc()
  function ask(message) {
    return new Promise((resolve) => {
      const modal = document.createElement("div");
      modal.className = "modal";
      const form = document.createElement("form");
      form.className = "modal-card";
      const title = document.createElement("h2");
      title.textContent = "Nhập mã truy cập";
      const hint = document.createElement("p");
      hint.className = "muted";
      hint.textContent = message || "Mỗi người có một mã riêng do admin cấp. Chỉ cần nhập một lần trên máy này.";
      const input = document.createElement("input");
      input.type = "password";
      input.required = true;
      input.autocomplete = "current-password";
      input.placeholder = "Mã truy cập";
      input.id = "accessTokenInput";
      const button = document.createElement("button");
      button.className = "btn primary";
      button.type = "submit";
      button.textContent = "Vào";
      form.append(title, hint, input, button);
      modal.append(form);
      document.body.append(modal);
      input.focus();

      form.addEventListener("submit", (e) => {
        e.preventDefault();
        const value = input.value.trim();
        if (!value) return;
        modal.remove();
        set(value);
        resolve(value);
      });
    });
  }

  async function ensure(message) {
    return get() || ask(message);
  }

  const headers = (extra = {}) => ({ "X-Access-Token": get() || "", ...extra });

  // Quên mã trên máy này (vd đổi người dùng) rồi tải lại trang để nhập mã mới
  function signOut() {
    clear();
    location.reload();
  }

  window.ThreadsAuth = { get, set, clear, ask, ensure, headers, signOut };
})();
