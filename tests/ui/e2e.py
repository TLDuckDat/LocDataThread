"""Kiểm thử giao diện end-to-end (Playwright + Edge).

Bao phủ: đăng nhập bằng mã truy cập, XSS (H2), token phiên khi 401 / reconnect (H1), khóa + "Vẫn chuyển"
chỉ admin + hoàn tác + giữ vị trí (H4-H6), chuỗi F (loại lan xuống con, khôi phục cả chuỗi), chia việc
(admin chia / chuyển, "Phần của tôi", tự nhảy bình luận kế tiếp), Kho phụ và trang xuất file.

Yêu cầu:
  - pip install playwright psycopg2-binary
  - Database threads_toxic_test đã chạy schema.sql + db/migrations (tools/start_test_db.ps1)
  - App chạy tại http://localhost:5055, trỏ vào threads_toxic_test, với tài khoản test của script này:
      python tests/ui/e2e.py --env          # in các biến Auth__Users__* (dạng KEY=VALUE)
      (đặt các biến đó + ConnectionStrings__Default rồi)
      dotnet run --project src/ThreadsToxic.Web --urls http://localhost:5055

Script chỉ ghi vào database threads_toxic_test và dọn sạch khi kết thúc.
"""
import hashlib
import io
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

URL = os.environ.get("THREADS_UI_URL", "http://localhost:5055/")
DSN = os.environ.get(
    "THREADS_TEST_DSN",
    "host=localhost port=55432 dbname=threads_toxic_test user=postgres password=postgres")

HOSTILE = "<img src=x onerror=window.__xss=1>"   # tên tài khoản độc hại: phải hiện dạng văn bản
# Tài khoản test (tên, vai trò); mã truy cập của người thứ i là token(i)
USERS = [("Minh", "admin"), ("Lan", "member"), ("Retry", "member"), ("Reco", "member"), (HOSTILE, "member")]


def token(name):
    index = [u[0] for u in USERS].index(name)
    return f"e2e-access-token-{index}"


def env_lines():
    for i, (name, role) in enumerate(USERS):
        yield f"Auth__Users__{i}__Name={name}"
        yield f"Auth__Users__{i}__Role={role}"
        yield f"Auth__Users__{i}__TokenSha256={hashlib.sha256(token(name).encode()).hexdigest()}"


if __name__ == "__main__" and "--env" in sys.argv:
    print("\n".join(env_lines()))
    sys.exit(0)

import psycopg2  # noqa: E402
from playwright.sync_api import expect, sync_playwright  # noqa: E402

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
        cur.execute("DELETE FROM moderation_audits; DELETE FROM comments; DELETE FROM posts; DELETE FROM review_queue;")


def insert_comment(cur, rid, content, author, url, score, words="[]", post="p", level=0, chain=None):
    f = (chain or [])[:level] + [content]
    f += [""] * (4 - len(f))
    cur.execute(
        """INSERT INTO comments (id, post_id, post_url, comment_url, author_username, content,
                                 reply_level, is_reply, toxic_score, f0, f1, f2, f3, analysis_data)
           VALUES (%s, %s, 'https://example.test/p', %s, %s, %s, %s, %s, %s, %s, %s, %s, %s,
                   jsonb_build_object('matched_words', %s::jsonb, 'matched_emojis', '[]'::jsonb,
                                      'categories', '[]'::jsonb, 'has_emoji_slang', false,
                                      'is_auto_toxic', false))""",
        (rid, post, url, author, content, level, level > 0, score, *f, words))


def seed():
    with db() as conn, conn.cursor() as cur:
        # Dòng độc hại để kiểm tra XSS
        insert_comment(cur, "x_xss", "<img src=x onerror=\"window.__xss='content'\"> <b>đậm</b>",
                       "<script>window.__xss='script'</script>", "javascript:window.__xss='href'", 1.0,
                       '["<svg onload=window.__xss=\'word\'>"]')
        # Cả comment_url lẫn post_url đều độc hại: không được có liên kết nào
        insert_comment(cur, "x_xss2", "bình luận có cả hai URL độc hại", "evil",
                       "javascript:window.__xss='href2'", 0.99)
        cur.execute("UPDATE comments SET post_url = 'data:text/html,<script>window.__xss=1</script>' "
                    "WHERE id = 'x_xss2'")
        for i in range(1, 41):
            insert_comment(cur, f"c_{i:02d}", f"Bình luận số {i}", f"user{i}",
                           f"https://www.threads.com/@user{i}/post/{i}", 0.9 - i / 100)
        # Luồng hội thoại cho kiểm tra chuỗi F: t0 (F0) -> t1 (F1) -> t2 (F2), ở bài viết riêng
        chain = ["Gốc của luồng t", "Trả lời cấp 1", "Trả lời cấp 2"]
        for level in range(3):
            insert_comment(cur, f"t{level}", chain[level], "chain", None, 0.5, post="pt", level=level, chain=chain)
        # Kho phụ: một bản ghi chờ quyết định
        cur.execute("""INSERT INTO review_queue (id, record_type, reason, record_id, post_id, post_url, content,
                f0, f1, f2, f3, reply_level, toxic_score, analysis_data, occurrences, crawler_ids)
            VALUES ('q_1', 'comment', 'non_vietnamese', 'q_1', 'pq', 'https://example.test/q', 'hello world',
                'hello world', '', '', '', 0, 0.1, '{}'::jsonb, 1, ARRAY['cr_q_1'])""")


def audits(comment_id):
    with db() as conn, conn.cursor() as cur:
        cur.execute("SELECT user_name, action FROM moderation_audits WHERE comment_id = %s ORDER BY id",
                    (comment_id,))
        return cur.fetchall()


def scalar(sql, *args):
    with db() as conn, conn.cursor() as cur:
        cur.execute(sql, args)
        row = cur.fetchone()
        return row[0] if row else None


def is_excluded(comment_id):
    return scalar("SELECT is_excluded FROM comments WHERE id = %s", comment_id)


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


def new_context(browser, access=None, capture_ws=False):
    ctx = browser.new_context(viewport={"width": 1280, "height": 900})
    if access:
        ctx.add_init_script(f"localStorage.setItem('threads-toxic-access', {access!r})")
    if capture_ws:
        ctx.add_init_script(CAPTURE_WS)
    return ctx


def open_as(browser, name, capture_ws=False, path=""):
    ctx = new_context(browser, token(name), capture_ws)
    page = ctx.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.goto(URL + path)
    if not path:
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
            test_login(browser)
            test_xss(browser)
            test_retry_once_on_401(browser)
            test_reconnect_refreshes_token(browser)
            test_locks_force_undo_and_scroll(browser)
            test_thread_chain(browser)
            test_assignment(browser)
            test_queue_and_export(browser)
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


def test_login(browser):
    print("Đăng nhập bằng mã truy cập")
    ctx = new_context(browser)
    page = ctx.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.goto(URL)
    expect(page.locator("#accessTokenInput")).to_be_visible(timeout=8000)
    check(page.locator(".row").count() == 0, "chưa nhập mã thì không thấy dữ liệu")

    page.fill("#accessTokenInput", "ma-sai")
    page.keyboard.press("Enter")
    expect(page.locator(".modal-card").filter(has_text="không đúng")).to_be_visible(timeout=8000)
    check(page.locator(".row").count() == 0, "mã sai thì vẫn không thấy dữ liệu")

    page.fill("#accessTokenInput", token("Lan"))
    page.keyboard.press("Enter")
    expect(page.locator("#status")).to_have_text("Đã kết nối", timeout=8000)
    expect(page.locator(".row").first).to_be_visible(timeout=8000)
    check(page.locator("#me").inner_text() == "Lan", f"hiện đúng tên theo mã ({page.locator('#me').inner_text()})")
    check(page.locator("#assignBtn").is_hidden(), "member không thấy nút Chia việc")

    # Mở lại trang: trình duyệt nhớ mã, vào thẳng
    page.reload()
    expect(page.locator("#status")).to_have_text("Đã kết nối", timeout=8000)
    check(page.locator("#accessTokenInput").count() == 0, "lần sau không phải nhập lại mã")

    # Đổi mã: quên mã trên máy này
    page.locator("#signOut").click()
    expect(page.locator("#accessTokenInput")).to_be_visible(timeout=8000)
    check(page.evaluate("localStorage.getItem('threads-toxic-access')") is None, "Đổi mã xóa mã đã nhớ")
    check(not errors, f"không có lỗi JS ({errors})")
    ctx.close()


def test_xss(browser):
    print("H2 — XSS")
    ctx, page, errors = open_as(browser, HOSTILE)
    page.wait_for_timeout(300)

    check(page.evaluate("window.__xss") is None, "không có script nào của payload được thực thi")
    check(page.locator("#list img, #presence img, #list svg, #list script, #presence script, #me img").count() == 0,
          "không có phần tử img/svg/script do dữ liệu tạo ra trong danh sách, presence, tên")
    check("<img src=x" in row(page, "x_xss").inner_text(), "nội dung độc hại hiện dạng văn bản thuần")
    check(HOSTILE in page.locator("#presence").inner_text(), "tên người dùng độc hại hiện dạng văn bản thuần")
    check(HOSTILE in page.locator("#me").inner_text(), "tên độc hại ở góc tài khoản cũng là văn bản")

    # comment_url độc hại nhưng post_url hợp lệ: chỉ được rơi về post_url, không bao giờ dùng javascript:
    row(page, "x_xss").click()
    expect(page.locator("#detail .chain")).to_be_visible()
    hrefs = page.locator("#detail a").evaluate_all("els => els.map(e => e.getAttribute('href'))")
    check(all(h.startswith("https://") for h in hrefs) and hrefs == ["https://example.test/p"],
          f"link chi tiết chỉ rơi về post_url hợp lệ, không dùng javascript: ({hrefs})")
    check(page.evaluate("window.__xss") is None, "mở chi tiết cũng không thực thi payload")

    row(page, "x_xss2").click()
    expect(page.locator("#detail .chain")).to_be_visible()
    check(page.locator("#detail a").count() == 0, "cả hai URL độc hại thì không hiện liên kết nào")
    check(page.evaluate("window.__xss") is None, "không có payload nào chạy sau khi mở dòng thứ hai")

    row(page, "c_01").click()
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

    def first_401(route):
        calls["n"] += 1
        if calls["n"] == 1:
            route.fulfill(status=401, body="")
        else:
            route.continue_()

    page.route("**/api/items/move", first_401)
    row(page, "c_01").click()
    page.keyboard.press("x")
    expect(page.locator(".toast").filter(has_text="Đã loại 1")).to_be_visible(timeout=8000)
    check(calls["n"] == 2, f"đúng 2 request move (1 bị 401 + 1 thử lại), thực tế {calls['n']}")
    check(is_excluded("c_01"), "thao tác thành công sau khi thử lại")
    check(audits("c_01") == [("Retry", "exclude")], "audit ghi đúng tên từ phiên mới")
    page.unroute("**/api/items/move")

    calls["n"] = 0
    page.route("**/api/items/move",
               lambda route: (calls.__setitem__("n", calls["n"] + 1), route.fulfill(status=401, body=""))[1])
    row(page, "c_02").click()
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

    page.evaluate("window.__sockets.forEach(s => s.close(4000, 'test'))")
    page.wait_for_timeout(500)
    expect(page.locator("#status")).to_have_text("Đã kết nối", timeout=30000)
    page.wait_for_timeout(1000)

    row(page, "c_03").click()
    page.keyboard.press("x")
    expect(page.locator(".toast").filter(has_text="Đã loại 1")).to_be_visible(timeout=8000)
    check(401 not in statuses, f"client đã cập nhật token sau reconnect nên không bị 401 ({statuses})")
    check(audits("c_03") == [("Reco", "exclude")], "audit ghi đúng tên sau reconnect")
    check(not errors, f"không có lỗi JS ({errors})")
    ctx.close()


def test_locks_force_undo_and_scroll(browser):
    print("H4/H5/H6 — khóa, vẫn chuyển (admin), hoàn tác, giữ vị trí")
    minh_ctx, minh, minh_err = open_as(browser, "Minh")
    lan_ctx, lan, lan_err = open_as(browser, "Lan")
    expect(lan.locator("#presence .chip")).to_have_count(2, timeout=8000)

    # --- H4: Minh giữ c_10, Lan (member) thử loại: bị chặn và KHÔNG có nút "Vẫn chuyển"
    row(minh, "c_10").click()
    expect(lan.locator('.row[data-id="c_10"] .lock')).to_contain_text("Minh", timeout=8000)
    lan.locator('.row[data-id="c_10"] input[type=checkbox]').check()
    lan.locator("#moveBtn").click()
    lan_toast = lan.locator(".toast").filter(has_text="đang được Minh xem")
    expect(lan_toast).to_be_visible(timeout=8000)
    check(not is_excluded("c_10"), "bình luận bị khóa không bị loại")
    check(lan_toast.locator("button", has_text="Vẫn chuyển").count() == 0, "member không có nút 'Vẫn chuyển'")
    lan.locator("#clearSel").click()

    # --- Lan giữ c_13, Minh (admin) thử loại: có nút "Vẫn chuyển"
    row(lan, "c_13").click()
    expect(minh.locator('.row[data-id="c_13"] .lock')).to_contain_text("Lan", timeout=8000)
    minh.locator('.row[data-id="c_13"] input[type=checkbox]').check()
    minh.locator("#moveBtn").click()
    toast = minh.locator(".toast").filter(has_text="đang được Lan xem")
    expect(toast).to_be_visible(timeout=8000)
    check(toast.locator("button", has_text="Vẫn chuyển").count() == 1, "admin có nút 'Vẫn chuyển'")
    toast.locator("button", has_text="Vẫn chuyển").click()
    expect(row(minh, "c_13")).to_have_count(0, timeout=8000)
    check(is_excluded("c_13"), "'Vẫn chuyển' vượt khóa thành công")
    check(("Minh", "force_exclude") in audits("c_13"), f"audit đánh dấu force: {audits('c_13')}")
    expect(lan.locator(".toast").filter(has_text="Minh đã loại 1").last).to_be_visible(timeout=8000)
    check(True, "Lan nhận thông báo real-time")
    row(minh, "c_10").click()   # đổi focus để không giữ khóa c_10 nữa

    # --- H5: hoàn tác thao tác của chính mình
    before = int(lan.locator("#countActive").inner_text().replace(".", ""))
    row(lan, "c_11").click()
    lan.keyboard.press("x")
    undo_toast = lan.locator(".toast").filter(has_text="Đã loại 1").last
    expect(undo_toast.locator("button", has_text="Hoàn tác")).to_be_visible(timeout=8000)
    expect(row(lan, "c_11")).to_have_count(0, timeout=8000)
    undo_toast.locator("button", has_text="Hoàn tác").click()
    expect(row(lan, "c_11")).to_be_visible(timeout=8000)
    after = int(lan.locator("#countActive").inner_text().replace(".", ""))
    check(not is_excluded("c_11") and after == before, f"hoàn tác đưa trạng thái và số đếm về cũ ({before} -> {after})")

    # --- H5: id đã bị người khác thao tác thì bỏ qua và báo
    # (c_20: xa c_11 vì sau khi loại c_11, focus của Lan tự nhảy sang dòng kế tiếp và giữ khóa dòng đó)
    # Bỏ thông báo cũ (của lần "Vẫn chuyển" ở trên, còn hiện 8 giây) để không bấm nhầm nút Hoàn tác của nó
    minh.evaluate("document.querySelectorAll('.toast').forEach(t => t.remove())")
    row(minh, "c_20").click()
    minh.keyboard.press("x")
    expect(row(minh, "c_20")).to_have_count(0, timeout=8000)
    minh_toast = minh.locator(".toast").filter(has_text="Đã loại 1").last
    expect(minh_toast.locator("button", has_text="Hoàn tác")).to_be_visible(timeout=8000)
    with db() as conn, conn.cursor() as cur:   # mô phỏng Lan khôi phục rồi loại lại
        cur.execute("""INSERT INTO moderation_audits (comment_id, user_name, action)
                       VALUES ('c_20', 'Lan', 'restore'), ('c_20', 'Lan', 'exclude')""")
    minh_toast.locator("button", has_text="Hoàn tác").click()
    expect(minh.locator(".toast").filter(has_text="bị người khác thao tác")).to_be_visible(timeout=8000)
    check(is_excluded("c_20"), "bình luận đã bị người khác thao tác được giữ nguyên")

    # --- H6: giữ focus và vị trí cuộn khi danh sách tải lại
    minh.evaluate("window.scrollTo(0, 400)")
    minh.wait_for_timeout(200)
    visible = minh.evaluate("""() => {
        const rows = [...document.querySelectorAll('#list .row')];
        return rows.filter(r => { const b = r.getBoundingClientRect(); return b.top > 120 && b.bottom < innerHeight; })
                   .map(r => r.dataset.id); }""")
    target = visible[len(visible) // 2]
    row(minh, target).click()
    top_before = minh.evaluate(f"document.querySelector('.row[data-id=\"{target}\"]').getBoundingClientRect().top")
    order = minh.evaluate("[...document.querySelectorAll('#list .row')].map(r => r.dataset.id)")
    above = next(i for i in order[2:] if order.index(i) < order.index(target) and i.startswith("c_"))
    row(lan, above).click()
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
    row(minh, "c_30").click()
    with db() as conn, conn.cursor() as cur:
        cur.execute("UPDATE comments SET is_excluded = TRUE, excluded_by = 'Lan', excluded_at = NOW() WHERE id = 'c_30'")
    row(lan, "c_31").click()
    lan.keyboard.press("x")
    expect(row(minh, "c_30")).to_have_count(0, timeout=8000)
    check(minh.locator(".row.is-focus").count() == 0, "bình luận focus biến mất thì không tự nhảy focus sang dòng khác")

    minh.screenshot(path="tests/ui/screenshot_minh.png")
    check(not minh_err, f"Minh không có lỗi JS ({minh_err})")
    check(not lan_err, f"Lan không có lỗi JS ({lan_err})")
    minh_ctx.close()
    lan_ctx.close()


def test_thread_chain(browser):
    print("Chuỗi F — loại lan xuống con, khôi phục bị chặn, khôi phục cả chuỗi")
    ctx, page, errors = open_as(browser, "Lan")

    row(page, "t1").click()
    check("loại kèm mọi trả lời" in page.locator("#detail").inner_text(), "chi tiết báo loại sẽ kéo theo trả lời")
    page.keyboard.press("x")
    expect(page.locator(".toast").filter(has_text="Kèm 1 trả lời")).to_be_visible(timeout=8000)
    check(is_excluded("t1") and is_excluded("t2") and not is_excluded("t0"), "t1 và t2 bị loại, t0 giữ nguyên")
    check(("Lan", "exclude_cascade") in audits("t2"), f"audit t2 là exclude_cascade: {audits('t2')}")
    expect(row(page, "t2")).to_have_count(0, timeout=8000)

    page.locator('.tab[data-bucket="excluded"]').click()
    expect(row(page, "t2")).to_be_visible(timeout=8000)
    row(page, "t2").click()
    page.keyboard.press("x")
    toast = page.locator(".toast").filter(has_text="bình luận cha vẫn ở")
    expect(toast).to_be_visible(timeout=8000)
    check(is_excluded("t2"), "không khôi phục được t2 khi cha t1 còn bị loại")
    toast.locator("button", has_text="Khôi phục cả chuỗi").click()
    expect(page.locator(".toast").filter(has_text="Kèm 1 bình luận cha")).to_be_visible(timeout=8000)
    check(not is_excluded("t1") and not is_excluded("t2"), "khôi phục cả chuỗi đưa t1, t2 về Đang lọc")
    page.locator('.tab[data-bucket="active"]').click()
    check(not errors, f"không có lỗi JS ({errors})")
    ctx.close()


def test_assignment(browser):
    print("Chia việc — admin chia/chuyển, Phần của tôi, tự nhảy bình luận kế tiếp")
    minh_ctx, minh, minh_err = open_as(browser, "Minh")
    minh.on("dialog", lambda d: d.accept())
    check(minh.locator("#assignBtn").is_visible(), "admin thấy nút Chia việc")
    minh.locator("#assignBtn").click()
    expect(minh.locator("#assignRows tr")).to_have_count(len(USERS), timeout=8000)
    minh.locator("#splitBtn").click()
    expect(minh.locator(".toast").filter(has_text="Đã chia việc")).to_be_visible(timeout=8000)
    unassigned = scalar("""SELECT COUNT(*) FROM comments
                           WHERE assigned_to IS NULL AND NOT is_excluded AND reviewed_by IS NULL""")
    check(unassigned == 0, f"không còn bình luận chưa giao ({unassigned})")
    check(scalar("SELECT COUNT(*) FROM comments WHERE assigned_to = 'Minh'") == 0, "admin không tự nhận việc")
    check(scalar("SELECT COUNT(DISTINCT assigned_to) FROM comments WHERE post_id = 'pt'") == 1,
          "cả luồng t0..t2 về một người")

    # Chuyển phần chưa làm của người giữ bài 'p' sang Lan qua giao diện
    owner = scalar("SELECT assigned_to FROM comments WHERE post_id = 'p' AND assigned_to IS NOT NULL LIMIT 1")
    if owner != "Lan":
        minh.select_option("#transferFrom", owner)
        minh.select_option("#transferTo", "Lan")
        minh.locator("#transferBtn").click()
        expect(minh.locator(".toast").filter(has_text="sang Lan")).to_be_visible(timeout=8000)
    lan_count = scalar("""SELECT COUNT(*) FROM comments
                          WHERE assigned_to = 'Lan' AND NOT is_excluded AND reviewed_by IS NULL""")
    check(lan_count > 10, f"Lan có phần việc chưa làm ({lan_count})")
    minh.locator("#assignClose").click()

    # Lan mở trang: mặc định "Phần của tôi" + "Chưa ai xử lý", sắp theo luồng
    lan_ctx, lan, lan_err = open_as(browser, "Lan")
    check(lan.locator("#assigned").input_value() == "me", "mặc định xem Phần của tôi")
    check(lan.locator("#review").input_value() == "unreviewed", "mặc định chỉ bình luận chưa xử lý")
    expect(lan.locator("#myProgress")).to_contain_text("Phần của tôi: còn", timeout=8000)
    ids = lan.evaluate("[...document.querySelectorAll('#list .row')].map(r => r.dataset.id)")
    mine = {r for (r,) in rows_of("SELECT id FROM comments WHERE assigned_to = 'Lan' AND reviewed_by IS NULL AND NOT is_excluded")}
    check(ids and set(ids) <= mine, "danh sách chỉ gồm bình luận được giao cho Lan")

    # Tự nhảy: giữ lại bình luận đang xem -> focus sang bình luận kế tiếp
    first, second = ids[0], ids[1]
    row(lan, first).click()
    lan.keyboard.press("a")
    expect(lan.locator(".row.is-focus")).to_have_attribute("data-id", second, timeout=8000)
    check(scalar("SELECT reviewed_by FROM comments WHERE id = %s", first) == "Lan", "đã giữ lại bình luận đầu")
    lan.keyboard.press("x")
    expect(row(lan, second)).to_have_count(0, timeout=8000)
    third = ids[2]
    expect(lan.locator(".row.is-focus")).to_have_attribute("data-id", third, timeout=8000)
    check(True, "sau khi loại, focus tự sang bình luận kế tiếp")

    check(not minh_err, f"Minh không có lỗi JS ({minh_err})")
    check(not lan_err, f"Lan không có lỗi JS ({lan_err})")
    minh_ctx.close()
    lan_ctx.close()


def rows_of(sql, *args):
    with db() as conn, conn.cursor() as cur:
        cur.execute(sql, args)
        return cur.fetchall()


def test_queue_and_export(browser):
    print("Kho phụ và trang xuất file")
    lan_ctx, lan, lan_err = open_as(browser, "Lan", path="queue.html")
    expect(lan.locator("#status")).to_have_text("Đã kết nối", timeout=8000)
    expect(lan.locator('.row[data-id="q_1"]')).to_be_visible(timeout=8000)
    check(lan.locator("#bulkBox").is_hidden(), "member không thấy thao tác hàng loạt ở Kho phụ")
    lan_ctx.close()

    minh_ctx, minh, minh_err = open_as(browser, "Minh", path="queue.html")
    expect(minh.locator("#status")).to_have_text("Đã kết nối", timeout=8000)
    expect(minh.locator('.row[data-id="q_1"]')).to_be_visible(timeout=8000)
    check(minh.locator("#bulkBox").is_visible(), "admin thấy thao tác hàng loạt")
    minh.locator('.row[data-id="q_1"]').click()
    minh.keyboard.press("r")
    expect(minh.locator('.row[data-id="q_1"]')).to_have_count(0, timeout=8000)
    check(scalar("SELECT decision FROM review_queue WHERE id = 'q_1'") == "rejected", "loại bản ghi kho phụ bằng phím R")
    minh_ctx.close()

    # Trang xuất: có mã thì xem trước được, không có mã thì phải nhập
    exp_ctx, exp, exp_err = open_as(browser, "Lan", path="export.html")
    expect(exp.locator("#previewBody")).to_be_visible(timeout=15000)
    check("dòng" in exp.locator("#previewInfo").inner_text(), "xem trước hiện thông tin dòng")
    exp.locator("#makeCsv").click()
    expect(exp.locator("#getCsv")).to_be_visible(timeout=30000)
    check(True, "tạo file CSV thành công với mã truy cập")
    exp_ctx.close()

    anon = new_context(browser)
    page = anon.new_page()
    page.goto(URL + "export.html")
    expect(page.locator("#accessTokenInput")).to_be_visible(timeout=8000)
    check(page.locator("#previewBody").is_hidden(), "chưa có mã thì trang xuất không tải dữ liệu")
    anon.close()
    check(not (lan_err or minh_err or exp_err), f"không có lỗi JS ({lan_err} {minh_err} {exp_err})")


if __name__ == "__main__":
    main()
