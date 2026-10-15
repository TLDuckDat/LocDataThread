"""Kiểm thử giao diện end-to-end (Playwright + Edge) cho H1, H2, H4, H5, H6.

Yêu cầu:
  - Postgres test trong Docker: powershell -ExecutionPolicy Bypass -File tools/start_test_db.ps1
  - App đang chạy tại http://localhost:5055 và trỏ vào database threads_toxic_test đó:
      $env:ConnectionStrings__Default = "Host=localhost;Port=55432;Database=threads_toxic_test;Username=postgres;Password=postgres"
      dotnet run --project src/ThreadsToxic.Web --urls http://localhost:5055
  - pip install playwright psycopg2-binary

Script chỉ ghi vào database threads_toxic_test và dọn sạch khi kết thúc.
"""
import io
import os
import sys

import psycopg2
from playwright.sync_api import expect, sync_playwright

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

URL = os.environ.get("THREADS_UI_URL", "http://localhost:5055/")
DSN = os.environ.get(
    "THREADS_TEST_DSN",
    "host=localhost port=55432 dbname=threads_toxic_test user=postgres password=postgres")

if "threads_toxic_test" not in DSN:
    raise SystemExit("DỪNG: DSN phải trỏ tới threads_toxic_test")

failures = []


def check(condition, message):
    print(("  ok:   " if condition else "  FAIL: ") + message)
    if not condition:
        failures.append(message)


def db():
    return psycopg2.connect(DSN)


def reset_db():
    with db() as conn, conn.cursor() as cur:
        cur.execute("DELETE FROM moderation_audits; DELETE FROM comments;")


def seed():
    rows = []
    # Dòng độc hại để kiểm tra XSS (điểm cao nhất nên nằm đầu danh sách)
    rows.append(("x_xss", "<img src=x onerror=\"window.__xss='content'\"> <b>đậm</b>",
                 "<script>window.__xss='script'</script>", "javascript:window.__xss='href'", 1.0, 0,
                 '["<svg onload=window.__xss=\'word\'>"]'))
    # Cả comment_url lẫn post_url đều độc hại: không được có liên kết nào
    rows.append(("x_xss2", "bình luận có cả hai URL độc hại", "evil", "javascript:window.__xss='href2'",
                 0.99, 0, "[]"))
    for i in range(1, 41):
        rows.append((f"c_{i:02d}", f"Bình luận số {i}", f"user{i}",
                     f"https://www.threads.com/@user{i}/post/{i}", 0.9 - i / 100, 0, "[]"))
    with db() as conn, conn.cursor() as cur:
        for rid, content, author, url, score, level, words in rows:
            cur.execute(
                """INSERT INTO comments (id, post_id, post_url, comment_url, author_username, content,
                                         reply_level, toxic_score, analysis_data, f0, f1, f2, f3)
                   VALUES (%(id)s, 'p', 'https://example.test/p', %(url)s, %(author)s, %(content)s,
                           %(level)s, %(score)s,
                           jsonb_build_object('matched_words', %(words)s::jsonb, 'matched_emojis', '[]'::jsonb,
                                              'categories', '[]'::jsonb, 'has_emoji_slang', false,
                                              'is_auto_toxic', false),
                           -- chuỗi F0..F3 theo comments_chain_ck (migration 005)
                           %(content)s,
                           CASE WHEN %(level)s >= 1 THEN %(content)s ELSE '' END,
                           CASE WHEN %(level)s >= 2 THEN %(content)s ELSE '' END,
                           CASE WHEN %(level)s >= 3 THEN %(content)s ELSE '' END)""",
                {"id": rid, "url": url, "author": author, "content": content, "level": level,
                 "score": score, "words": words})
        cur.execute("UPDATE comments SET post_url = 'data:text/html,<script>window.__xss=1</script>' "
                    "WHERE id = 'x_xss2'")


def audits(comment_id):
    with db() as conn, conn.cursor() as cur:
        cur.execute("SELECT user_name, action FROM moderation_audits WHERE comment_id = %s ORDER BY id",
                    (comment_id,))
        return cur.fetchall()


def is_excluded(comment_id):
    with db() as conn, conn.cursor() as cur:
        cur.execute("SELECT is_excluded FROM comments WHERE id = %s", (comment_id,))
        return cur.fetchone()[0]


CAPTURE_WS = """
(() => {
  const Original = window.WebSocket;
  window.__sockets = [];
  function Patched(...args) {
    const socket = new Original(...args);
    window.__sockets.push(socket);
    return socket;
  }
  Patched.prototype = Original.prototype;
  for (const key of ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED']) Patched[key] = Original[key];
  window.WebSocket = Patched;
})();
"""


def open_as(browser, name, viewport=None, capture_ws=False):
    ctx = browser.new_context(viewport=viewport or {"width": 1280, "height": 900})
    ctx.add_init_script(f"localStorage.setItem('threads-toxic-name', {name!r})")
    if capture_ws:
        ctx.add_init_script(CAPTURE_WS)
    page = ctx.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.goto(URL)
    expect(page.locator(".row").first).to_be_visible()
    expect(page.locator("#status")).to_have_text("Đã kết nối", timeout=8000)
    return ctx, page, errors


def row(page, comment_id):
    return page.locator(f'.row[data-id="{comment_id}"]')


def main():
    reset_db()
    seed()
    try:
        with sync_playwright() as p:
            browser = p.chromium.launch(channel="msedge", headless=True)
            test_xss(browser)
            test_retry_once_on_401(browser)
            test_reconnect_refreshes_token(browser)
            test_locks_force_undo_and_scroll(browser)
            browser.close()
    finally:
        reset_db()

    print()
    if failures:
        print(f"{len(failures)} kiểm tra THẤT BẠI:")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("Tất cả kiểm tra giao diện đạt.")


def test_xss(browser):
    print("H2 — XSS")
    hostile_name = "<img src=x onerror=window.__xss=1>"   # 34 ký tự: vừa giới hạn 40 của server
    ctx, page, errors = open_as(browser, hostile_name)
    page.wait_for_timeout(300)

    check(page.evaluate("window.__xss") is None, "không có script nào của payload được thực thi")
    check(page.locator("#list img, #presence img, #list svg, #list script, #presence script").count() == 0,
          "không có phần tử img/svg/script do dữ liệu tạo ra trong danh sách và presence")
    check("<img src=x" in page.locator("#list .row").first.inner_text(),
          "nội dung độc hại hiện dạng văn bản thuần")
    check(hostile_name in page.locator("#presence").inner_text(), "tên người dùng độc hại hiện dạng văn bản thuần")

    # comment_url độc hại nhưng post_url hợp lệ: chỉ được rơi về post_url, không bao giờ dùng javascript:
    page.locator('.row[data-id="x_xss"]').click()
    expect(page.locator("#detail .chain")).to_be_visible()
    hrefs = page.locator("#detail a").evaluate_all("els => els.map(e => e.getAttribute('href'))")
    check(all(h.startswith("https://") for h in hrefs) and hrefs == ["https://example.test/p"],
          f"link chi tiết chỉ rơi về post_url hợp lệ, không dùng javascript: ({hrefs})")
    check(page.evaluate("window.__xss") is None, "mở chi tiết cũng không thực thi payload")

    # Cả hai URL độc hại: không có liên kết nào
    page.locator('.row[data-id="x_xss2"]').click()
    expect(page.locator("#detail .chain")).to_be_visible()
    check(page.locator("#detail a").count() == 0, "cả hai URL độc hại thì không hiện liên kết nào")
    check(page.evaluate("window.__xss") is None, "không có payload nào chạy sau khi mở dòng thứ hai")

    # Liên kết hợp lệ vẫn dùng được
    page.locator('.row[data-id="c_01"]').click()
    link = page.locator("#detail a")
    check(link.count() == 1 and link.get_attribute("href").startswith("https://www.threads.com/"),
          "liên kết https hợp lệ vẫn hiển thị")
    check("noopener" in (link.get_attribute("rel") or ""), "liên kết mở tab mới có rel=noopener")
    check(not errors, f"không có lỗi JS ({errors})")
    ctx.close()


def test_retry_once_on_401(browser):
    print("H1 — 401 thì Join lại và thử lại đúng một lần")
    ctx, page, errors = open_as(browser, "Retry")
    calls = {"n": 0}

    # Lần 1 trả 401 giả, lần 2 đi tiếp tới server thật
    def first_401(route):
        calls["n"] += 1
        if calls["n"] == 1:
            route.fulfill(status=401, body="")
        else:
            route.continue_()

    page.route("**/api/items/move", first_401)
    page.locator('.row[data-id="c_01"]').click()
    page.keyboard.press("x")
    expect(page.locator(".toast").filter(has_text="Đã loại 1")).to_be_visible(timeout=8000)
    check(calls["n"] == 2, f"đúng 2 request move (1 bị 401 + 1 thử lại), thực tế {calls['n']}")
    check(is_excluded("c_01"), "thao tác thành công sau khi thử lại")
    check(audits("c_01") == [("Retry", "exclude")], "audit ghi đúng tên từ phiên mới")
    page.unroute("**/api/items/move")

    # Luôn 401: chỉ thử lại đúng một lần rồi báo lỗi
    calls["n"] = 0
    page.route("**/api/items/move", lambda route: (calls.__setitem__("n", calls["n"] + 1), route.fulfill(status=401, body=""))[1])
    page.locator('.row[data-id="c_02"]').click()
    page.keyboard.press("x")
    expect(page.locator(".toast").filter(has_text="Thao tác thất bại")).to_be_visible(timeout=8000)
    page.wait_for_timeout(500)
    check(calls["n"] == 2, f"không thử lại quá một lần (tổng {calls['n']} request)")
    check(not is_excluded("c_02"), "không có gì thay đổi khi luôn bị 401")
    page.unroute("**/api/items/move")
    check(not errors, f"không có lỗi JS ({errors})")
    ctx.close()


def test_reconnect_refreshes_token(browser):
    print("H1 — sau reconnect client dùng token mới")
    ctx, page, errors = open_as(browser, "Reco", capture_ws=True)
    statuses = []
    page.on("response", lambda r: statuses.append(r.status) if "/api/items/move" in r.url else None)
    check(page.evaluate("window.__sockets.length") >= 1, "đã bắt được WebSocket của SignalR")

    # Đóng socket với mã lỗi: SignalR coi là mất kết nối và tự reconnect bằng connection id mới.
    page.evaluate("window.__sockets.forEach(s => s.close(4000, 'test'))")
    page.wait_for_timeout(500)
    expect(page.locator("#status")).to_have_text("Đã kết nối", timeout=30000)
    page.wait_for_timeout(1000)

    page.locator('.row[data-id="c_03"]').click()
    page.keyboard.press("x")
    expect(page.locator(".toast").filter(has_text="Đã loại 1")).to_be_visible(timeout=8000)
    check(401 not in statuses, f"client đã cập nhật token sau reconnect nên không bị 401 ({statuses})")
    check(audits("c_03") == [("Reco", "exclude")], "audit ghi đúng tên sau reconnect")
    check(not errors, f"không có lỗi JS ({errors})")
    ctx.close()


def test_locks_force_undo_and_scroll(browser):
    print("H4/H5/H6 — khóa, vẫn chuyển, hoàn tác, giữ vị trí")
    minh_ctx, minh, minh_err = open_as(browser, "Minh")
    lan_ctx, lan, lan_err = open_as(browser, "Lan")
    expect(lan.locator("#presence .chip")).to_have_count(2, timeout=8000)

    # --- H4: Minh giữ c_10, Lan thử loại
    minh.locator('.row[data-id="c_10"]').click()
    expect(lan.locator('.row[data-id="c_10"] .lock')).to_contain_text("Minh", timeout=8000)
    lan.locator('.row[data-id="c_10"] input[type=checkbox]').check()
    lan.locator("#moveBtn").click()
    toast = lan.locator(".toast").filter(has_text="đang được Minh xem")
    expect(toast).to_be_visible(timeout=8000)
    check(not is_excluded("c_10"), "bình luận bị khóa không bị loại")
    check(toast.locator("button", has_text="Vẫn chuyển").count() == 1, "thông báo có nút 'Vẫn chuyển'")

    # --- Vẫn chuyển (force)
    toast.locator("button", has_text="Vẫn chuyển").click()
    expect(row(lan, "c_10")).to_have_count(0, timeout=8000)
    check(is_excluded("c_10"), "'Vẫn chuyển' vượt khóa thành công")
    check(("Lan", "force_exclude") in audits("c_10"), f"audit đánh dấu force: {audits('c_10')}")
    expect(minh.locator(".toast").filter(has_text="Lan đã loại 1").last).to_be_visible(timeout=8000)
    check(True, "Minh nhận thông báo real-time")

    # --- H5: hoàn tác thao tác của chính mình
    before = int(lan.locator("#countActive").inner_text().replace(".", ""))
    lan.locator('.row[data-id="c_11"]').click()
    lan.keyboard.press("x")
    undo_toast = lan.locator(".toast").filter(has_text="Đã loại 1").last
    expect(undo_toast.locator("button", has_text="Hoàn tác")).to_be_visible(timeout=8000)
    expect(row(lan, "c_11")).to_have_count(0, timeout=8000)
    undo_toast.locator("button", has_text="Hoàn tác").click()
    expect(row(lan, "c_11")).to_be_visible(timeout=8000)
    after = int(lan.locator("#countActive").inner_text().replace(".", ""))
    check(not is_excluded("c_11") and after == before, f"hoàn tác đưa trạng thái và số đếm về cũ ({before} -> {after})")

    # --- H5: id đã bị người khác thao tác thì bỏ qua và báo
    minh.locator('.row[data-id="c_12"]').click()
    minh.keyboard.press("x")
    minh_toast = minh.locator(".toast").filter(has_text="Đã loại 1").last
    expect(minh_toast.locator("button", has_text="Hoàn tác")).to_be_visible(timeout=8000)
    with db() as conn, conn.cursor() as cur:   # mô phỏng Lan khôi phục rồi loại lại
        cur.execute("""INSERT INTO moderation_audits (comment_id, user_name, action)
                       VALUES ('c_12', 'Lan', 'restore'), ('c_12', 'Lan', 'exclude')""")
    minh_toast.locator("button", has_text="Hoàn tác").click()
    expect(minh.locator(".toast").filter(has_text="bị người khác thao tác")).to_be_visible(timeout=8000)
    check(is_excluded("c_12"), "bình luận đã bị người khác thao tác được giữ nguyên")

    # --- H6: giữ focus và vị trí cuộn khi danh sách tải lại
    minh.evaluate("window.scrollTo(0, 400)")
    minh.wait_for_timeout(200)
    visible = minh.evaluate("""() => {
        const rows = [...document.querySelectorAll('#list .row')];
        return rows.filter(r => { const b = r.getBoundingClientRect(); return b.top > 120 && b.bottom < innerHeight; })
                   .map(r => r.dataset.id); }""")
    target = visible[len(visible) // 2]
    minh.locator(f'.row[data-id="{target}"]').click()
    top_before = minh.evaluate(f"document.querySelector('.row[data-id=\"{target}\"]').getBoundingClientRect().top")
    # Lan loại một bình luận nằm PHÍA TRÊN dòng đang focus: nếu không neo vị trí thì dòng focus sẽ
    # bị đẩy lên khoảng một chiều cao dòng sau khi danh sách tải lại.
    order = minh.evaluate("[...document.querySelectorAll('#list .row')].map(r => r.dataset.id)")
    above = next(i for i in order[2:] if order.index(i) < order.index(target) and i.startswith("c_"))
    lan.locator(f'.row[data-id="{above}"]').click()
    lan.keyboard.press("x")
    expect(minh.locator(".toast").filter(has_text="Lan đã loại 1").last).to_be_visible(timeout=8000)
    expect(row(minh, above)).to_have_count(0, timeout=8000)
    check(order.index(above) < order.index(target), f"dòng bị loại ({above}) nằm phía trên dòng focus ({target})")
    minh.wait_for_timeout(600)
    top_after = minh.evaluate(f"document.querySelector('.row[data-id=\"{target}\"]')?.getBoundingClientRect().top")
    focused = minh.locator(".row.is-focus").get_attribute("data-id") if minh.locator(".row.is-focus").count() else None
    check(focused == target, f"focus vẫn ở bình luận cũ ({focused} / {target})")
    check(top_after is not None and abs(top_after - top_before) <= 3,
          f"vị trí hiển thị của dòng đang focus không nhảy ({top_before} -> {top_after})")

    # --- H6: nếu bình luận đang focus bị người khác chuyển -> bỏ focus, không nhảy sang dòng khác
    minh.locator('.row[data-id="c_30"]').click()
    with db() as conn, conn.cursor() as cur:
        cur.execute("UPDATE comments SET is_excluded = TRUE, excluded_by = 'Lan', excluded_at = NOW() WHERE id = 'c_30'")
    lan.locator('.row[data-id="c_31"]').click()
    lan.keyboard.press("x")   # kích hoạt một ItemsMoved để Minh tải lại
    expect(row(minh, "c_30")).to_have_count(0, timeout=8000)
    check(minh.locator(".row.is-focus").count() == 0, "bình luận focus biến mất thì không tự nhảy focus sang dòng khác")

    minh.screenshot(path="tests/ui/screenshot_minh.png")
    check(not minh_err, f"Minh không có lỗi JS ({minh_err})")
    check(not lan_err, f"Lan không có lỗi JS ({lan_err})")
    minh_ctx.close()
    lan_ctx.close()


if __name__ == "__main__":
    main()
