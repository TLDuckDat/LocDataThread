"""Nạp bình luận và bài viết từ SQLite (threads_data.db) vào PostgreSQL (threads_toxic_db).

Chạy:
    python tools/import_comments.py --sqlite "<đường dẫn>/threads_data.db"

Script idempotent, chạy lại an toàn:
- Bình luận mới được thêm vào danh sách "Đang lọc".
- Bình luận đã có (cùng id, thuộc dữ liệu chính) chỉ được điền lại các cột phục vụ xuất file
  (review_status, user_review, user_keywords, is_reply, source_rowid); nội dung, điểm và trạng thái
  lọc (đang lọc / đã loại) giữ nguyên.
- Bài viết (bảng posts) luôn được cập nhật theo SQLite.

Chuỗi hội thoại (migration 007): DB tự gắn bình luận cha cho bản trả lời mới theo chuỗi F0..F3.
- Nạp theo từng cấp (F0 rồi F1, F2, F3), mỗi câu lệnh chỉ một cấp, để cha luôn có trước con.
- Bản trả lời mới không tìm thấy đúng một bình luận cha bị bỏ qua và được liệt kê (không làm hỏng chuỗi).
- Bản trả lời mới có cha đã bị loại tự vào "Đã loại" (người thao tác 'system'); số lượng được báo ở cuối.
"""
import argparse
import json
import sqlite3

import psycopg2
from psycopg2.extras import execute_values

from db_env import default_dsn

BATCH_SIZE = 1000


def parse_json_list(raw):
    """Cột matched_words / matched_emojis / categories ở SQLite là chuỗi JSON."""
    if not raw:
        return []
    try:
        value = json.loads(raw)
        return value if isinstance(value, list) else []
    except (TypeError, json.JSONDecodeError):
        return []


def to_row(r):
    matched_words = parse_json_list(r["matched_words"])
    matched_emojis = parse_json_list(r["matched_emojis"])
    categories = parse_json_list(r["categories"])
    analysis = {
        "matched_words": matched_words,
        "matched_emojis": matched_emojis,
        "categories": categories,
        "has_emoji_slang": bool(r["has_emoji_slang"]),
        "is_auto_toxic": bool(r["is_toxic"]),
    }
    return (
        r["id"],
        r["post_id"],
        r["post_url"],
        r["comment_url"] or None,
        r["author_username"] or None,
        r["author_name"] or None,
        r["content"] or "",
        r["posted_at"] or None,
        r["likes"] or 0,
        r["reply_level"] or 0,
        r["parent_comment_id"] or None,
        r["f0"] or "",
        r["f1"] or "",
        r["f2"] or "",
        r["f3"] or "",
        r["toxic_score"] or 0.0,
        r["severity_vi"] or None,
        json.dumps(analysis, ensure_ascii=False),
        r["scraped_at"] or None,
        # Cột phục vụ xuất file: giữ nguyên giá trị (kể cả NULL) như bên crawler
        bool(r["is_reply"]),
        r["review_status"],
        r["review_status_vi"],
        r["user_review"],
        r["user_review_vi"],
        r["user_keywords"],
        r["source_rowid"],
    )


INSERT_SQL = """
    INSERT INTO comments (
        id, post_id, post_url, comment_url, author_username, author_name,
        content, posted_at, likes, reply_level, parent_comment_id,
        f0, f1, f2, f3, toxic_score, severity, analysis_data, scraped_at,
        is_reply, review_status, review_status_vi, user_review, user_review_vi,
        user_keywords, source_rowid
    ) VALUES %s
    ON CONFLICT (id) DO UPDATE SET
        is_reply = EXCLUDED.is_reply,
        review_status = EXCLUDED.review_status,
        review_status_vi = EXCLUDED.review_status_vi,
        user_review = EXCLUDED.user_review,
        user_review_vi = EXCLUDED.user_review_vi,
        user_keywords = EXCLUDED.user_keywords,
        source_rowid = EXCLUDED.source_rowid
    WHERE comments.source IS NULL
"""

# Bản trả lời MỚI (chưa có id trong PostgreSQL) không xác định được đúng một bình luận cha
ORPHAN_SQL = """
    SELECT v.id FROM (VALUES %s) AS v(id, post_id, reply_level, f0, f1, f2)
    WHERE NOT EXISTS (SELECT 1 FROM comments c WHERE c.id = v.id)
      AND find_thread_parent(v.post_id, v.reply_level::int, v.f0, v.f1, v.f2) IS NULL
"""

POST_SQL = """
    INSERT INTO posts (id, url, author_username, author_name, content, categories)
    VALUES %s
    ON CONFLICT (id) DO UPDATE SET
        url = EXCLUDED.url,
        author_username = EXCLUDED.author_username,
        author_name = EXCLUDED.author_name,
        content = EXCLUDED.content,
        categories = EXCLUDED.categories
"""


def import_posts(src, pg):
    rows = [
        (r["id"], r["url"], r["author_username"], r["author_name"], r["content"], r["categories"])
        for r in src.execute("SELECT id, url, author_username, author_name, content, categories FROM posts")
    ]
    for start in range(0, len(rows), BATCH_SIZE):
        execute_values(pg, POST_SQL, rows[start:start + BATCH_SIZE])
    return len(rows)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sqlite", required=True, help="Đường dẫn tới threads_data.db")
    parser.add_argument("--dsn", help="Chuỗi kết nối PostgreSQL (mặc định: Supabase trong .env)")
    args = parser.parse_args()
    args.dsn = args.dsn or default_dsn()

    src = sqlite3.connect(f"file:{args.sqlite}?mode=ro", uri=True)
    src.row_factory = sqlite3.Row
    cursor = src.execute("SELECT rowid AS source_rowid, * FROM comments ORDER BY reply_level, id")

    dst = psycopg2.connect(args.dsn)
    total = 0
    try:
        with dst, dst.cursor() as pg:
            posts = import_posts(src, pg)
            print(f"  đã nạp {posts} bài viết...")
            pg.execute("SELECT COUNT(*) FROM moderation_audits WHERE action = 'exclude_cascade' AND user_name = 'system'")
            cascaded_before = pg.fetchone()[0]

            orphans = []

            def flush(batch):
                nonlocal total
                if not batch:
                    return
                rows = batch
                if batch[0][9] > 0:  # reply_level: kiểm tra cha trước khi chèn
                    keys = [(r[0], r[1], r[9], r[11], r[12], r[13]) for r in batch]
                    missing = {row[0] for row in execute_values(pg, ORPHAN_SQL, keys, fetch=True)}
                    if missing:
                        orphans.extend(sorted(missing))
                        rows = [r for r in batch if r[0] not in missing]
                if rows:
                    execute_values(pg, INSERT_SQL, rows)
                total += len(rows)
                batch.clear()
                print(f"  đã xử lý {total} bình luận...")

            # cursor đã sắp theo reply_level: đổi cấp thì chèn hết cấp trước rồi mới sang cấp sau
            batch = []
            for record in cursor:
                row = to_row(record)
                if batch and (len(batch) >= BATCH_SIZE or batch[0][9] != row[9]):
                    flush(batch)
                batch.append(row)
            flush(batch)

            pg.execute("SELECT COUNT(*) FROM moderation_audits WHERE action = 'exclude_cascade' AND user_name = 'system'")
            cascaded = pg.fetchone()[0] - cascaded_before
            pg.execute("SELECT COUNT(*) FROM comments")
            stored = pg.fetchone()[0]
        print(f"Xong. Đã gửi {total} bình luận, {posts} bài viết; tổng số bình luận trong PostgreSQL: {stored}.")
        if cascaded:
            print(f"{cascaded} bình luận mới có bình luận cha đã bị loại nên vào thẳng 'Đã loại'.")
        if orphans:
            print(f"Bỏ qua {len(orphans)} bản trả lời mới không tìm thấy bình luận cha (chuỗi F không khớp):")
            for comment_id in orphans[:20]:
                print(f"  - {comment_id}")
            if len(orphans) > 20:
                print(f"  ... và {len(orphans) - 20} bản khác")
    finally:
        dst.close()
        src.close()


if __name__ == "__main__":
    main()
