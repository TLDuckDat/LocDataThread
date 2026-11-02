"""Nạp bình luận từ file CSV (định dạng của trang xuất: Bài viết, f0, f1, f2, Nội dung) vào PostgreSQL.

Chạy:
    python tools/import_csv.py --csv "<đường dẫn>/data.csv" --dry-run   # xem trước, không ghi gì
    python tools/import_csv.py --csv "<đường dẫn>/data.csv"

Mọi bình luận mới vào "Chưa lọc". Script idempotent, chạy lại an toàn: bình luận đã có (cùng id) giữ nguyên,
kể cả trạng thái lọc.

CSV không có id, nên id được dựng từ nội dung:
- post_id  = "csv-p-" + băm(nội dung bài viết). Dòng không có bài viết: mỗi luồng F0 là một bài riêng
  (để chia việc không dồn cả nghìn dòng vào một người và chuỗi F không bị lẫn giữa các luồng).
- id       = "csv-" + băm(post_id + chuỗi F0..F_L của chính bình luận).

Cấp bình luận suy từ chuỗi: f_k là ô F cuối cùng không rỗng; "Nội dung" bằng f_k thì bình luận ở cấp k
(F_k là chính nó), khác f_k thì ở cấp k+1 (trả lời của f_k, tức F3).

Chuỗi hội thoại (migration 007) cần cha có trước con:
- Cha chỉ có trong ô F của dòng con (dòng riêng của cha không có trong CSV) được dựng lại từ chuỗi F và báo số lượng.
- Dòng có chuỗi đứt (vd f2 có nhưng f1 rỗng) bị bỏ qua và được liệt kê.
- Nạp theo từng cấp (F0 rồi F1, F2, F3) trong một transaction: lỗi giữa chừng thì không ghi gì.
"""
import argparse
import csv
import hashlib
import json
import sys

import psycopg2
from psycopg2.extras import execute_values

from db_env import default_dsn

BATCH_SIZE = 1000
HEADER = ["Bài viết", "f0", "f1", "f2", "Nội dung"]
EMPTY_ANALYSIS = json.dumps({
    "matched_words": [], "matched_emojis": [], "categories": [],
    "has_emoji_slang": False, "is_auto_toxic": False,
})


def digest(*parts, length):
    return hashlib.sha1("\x1f".join(parts).encode("utf-8")).hexdigest()[:length]


def read_csv(path):
    """Trả về (nodes, posts, broken). nodes: {(post_id, chain): rowid}; chain = (F0, ..., F_L)."""
    with open(path, encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f)
        header = next(reader, None)
        if header != HEADER:
            raise SystemExit(f"Cột không đúng: {header}. Cần đúng thứ tự: {', '.join(HEADER)}")

        nodes, posts, broken = {}, {}, []
        for line_no, row in enumerate(reader, start=2):
            if len(row) != 5:
                broken.append((line_no, f"có {len(row)} cột"))
                continue
            post, f0, f1, f2, content = row
            fs = [f0, f1, f2]
            if not f0 or not content or (f1 and not f0) or (f2 and not f1):
                broken.append((line_no, "chuỗi F đứt (ô cấp trên rỗng) hoặc thiếu nội dung"))
                continue

            k = max(i for i in range(3) if fs[i])
            level = k if content == fs[k] else k + 1
            chain = tuple(fs[:level]) + (content,)

            post_id = "csv-p-" + (digest(post, length=16) if post else digest("", f0, length=16))
            posts.setdefault(post_id, post or None)
            nodes.setdefault((post_id, chain), line_no)
    return nodes, posts, broken


def add_missing_ancestors(nodes):
    """Dựng lại cha/ông chỉ xuất hiện trong ô F của dòng con. Trả về số bình luận được dựng thêm."""
    added = 0
    for (post_id, chain), rowid in list(nodes.items()):
        for depth in range(1, len(chain)):
            key = (post_id, chain[:depth])
            if key not in nodes:
                nodes[key] = rowid
                added += 1
            else:
                nodes[key] = min(nodes[key], rowid)
    return added


def to_row(post_id, chain, rowid):
    level = len(chain) - 1
    content = chain[-1]
    f = list(chain) + [""] * (4 - len(chain))
    return (
        "csv-" + digest(post_id, *chain, length=24),
        post_id,
        "",                 # post_url (CSV không có liên kết)
        content,
        level,
        f[0], f[1], f[2], f[3],
        EMPTY_ANALYSIS,
        level > 0,          # is_reply
        rowid,              # source_rowid: thứ tự dòng trong CSV, giữ thứ tự cây hội thoại khi xuất
    )


INSERT_SQL = """
    INSERT INTO comments (
        id, post_id, post_url, content, reply_level, f0, f1, f2, f3, analysis_data, is_reply, source_rowid
    ) VALUES %s
    ON CONFLICT (id) DO NOTHING
"""

POST_SQL = """
    INSERT INTO posts (id, content) VALUES %s
    ON CONFLICT (id) DO NOTHING
"""


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--csv", required=True, help="File CSV: Bài viết, f0, f1, f2, Nội dung")
    parser.add_argument("--dsn", help="Chuỗi kết nối PostgreSQL (mặc định: ConnectionStrings__Default trong .env)")
    parser.add_argument("--dry-run", action="store_true", help="Chỉ đọc file và in thống kê, không ghi database")
    parser.add_argument("--allow-mixed", action="store_true",
                        help="Cho nạp khi database đã có bình luận không phải từ CSV (có thể trùng nội dung)")
    args = parser.parse_args()

    nodes, posts, broken = read_csv(args.csv)
    from_file = len(nodes)
    added = add_missing_ancestors(nodes)
    rows = sorted((to_row(p, chain, rowid) for (p, chain), rowid in nodes.items()), key=lambda r: (r[4], r[11]))

    levels = [sum(1 for r in rows if r[4] == lvl) for lvl in range(4)]
    print(f"Đọc {from_file} bình luận, {len(posts)} bài viết từ {args.csv}.")
    print(f"  theo cấp (gồm phần dựng thêm): F0 {levels[0]} · F1 {levels[1]} · F2 {levels[2]} · F3 {levels[3]}")
    if added:
        print(f"  dựng thêm {added} bình luận cha/ông chỉ có trong ô F của dòng con (để chuỗi liền mạch)")
    if broken:
        print(f"  bỏ qua {len(broken)} dòng lỗi:")
        for line_no, reason in broken[:20]:
            print(f"    - dòng {line_no}: {reason}")
        if len(broken) > 20:
            print(f"    ... và {len(broken) - 20} dòng khác")
    if args.dry_run:
        print("Chạy thử (--dry-run): chưa ghi gì vào database.")
        return

    dst = psycopg2.connect(args.dsn or default_dsn())
    try:
        with dst, dst.cursor() as pg:
            pg.execute("SELECT COUNT(*) FROM comments WHERE id NOT LIKE 'csv-%'")
            others = pg.fetchone()[0]
            if others and not args.allow_mixed:
                raise SystemExit(
                    f"Database đã có {others} bình luận không phải từ CSV. Nạp thêm CSV có thể làm trùng nội dung "
                    f"(id khác nhau). Sao lưu rồi dùng database trống, hoặc thêm --allow-mixed nếu chắc chắn.")
            pg.execute("SELECT COUNT(*) FROM comments")
            before = pg.fetchone()[0]

            post_rows = sorted(posts.items())
            for start in range(0, len(post_rows), BATCH_SIZE):
                execute_values(pg, POST_SQL, post_rows[start:start + BATCH_SIZE])

            # rows đã sắp theo cấp: mỗi lô chỉ một cấp để cha luôn được chèn ở câu lệnh trước con
            done = 0
            for level in range(4):
                level_rows = [r for r in rows if r[4] == level]
                for start in range(0, len(level_rows), BATCH_SIZE):
                    execute_values(pg, INSERT_SQL, level_rows[start:start + BATCH_SIZE])
                    done += len(level_rows[start:start + BATCH_SIZE])
                    print(f"  đã xử lý {done}/{len(rows)} bình luận...")

            pg.execute("SELECT COUNT(*) FROM comments")
            after = pg.fetchone()[0]
        print(f"Xong. Thêm mới {after - before} bình luận (đã có từ trước: {len(rows) - (after - before)}); "
              f"tổng trong PostgreSQL: {after}. Tất cả bình luận mới ở mục 'Chưa lọc'.")
    finally:
        dst.close()


if __name__ == "__main__":
    main()
