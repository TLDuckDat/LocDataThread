"""Nạp bình luận từ SQLite (threads_data.db) vào PostgreSQL (threads_toxic_db).

Chạy:
    python tools/import_comments.py --sqlite "<đường dẫn>/threads_data.db"

Script idempotent: bình luận đã có (cùng id) sẽ được bỏ qua.
"""
import argparse
import json
import sqlite3

import psycopg2
from psycopg2.extras import execute_values

BATCH_SIZE = 1000

PG_DSN = "host=localhost port=5432 dbname=threads_toxic_db user=postgres"


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
    )


INSERT_SQL = """
    INSERT INTO comments (
        id, post_id, post_url, comment_url, author_username, author_name,
        content, posted_at, likes, reply_level, parent_comment_id,
        f0, f1, f2, f3, toxic_score, severity, analysis_data, scraped_at
    ) VALUES %s
    ON CONFLICT (id) DO NOTHING
"""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sqlite", required=True, help="Đường dẫn tới threads_data.db")
    parser.add_argument("--dsn", default=PG_DSN, help="Chuỗi kết nối PostgreSQL")
    args = parser.parse_args()

    src = sqlite3.connect(f"file:{args.sqlite}?mode=ro", uri=True)
    src.row_factory = sqlite3.Row
    cursor = src.execute("SELECT * FROM comments ORDER BY reply_level, id")

    dst = psycopg2.connect(args.dsn)
    total = 0
    try:
        with dst, dst.cursor() as pg:
            batch = []
            for record in cursor:
                batch.append(to_row(record))
                if len(batch) >= BATCH_SIZE:
                    execute_values(pg, INSERT_SQL, batch)
                    total += len(batch)
                    batch.clear()
                    print(f"  đã xử lý {total} bình luận...")
            if batch:
                execute_values(pg, INSERT_SQL, batch)
                total += len(batch)
            pg.execute("SELECT COUNT(*) FROM comments")
            stored = pg.fetchone()[0]
        print(f"Xong. Đã gửi {total} bản ghi; tổng số bình luận trong PostgreSQL: {stored}.")
    finally:
        dst.close()
        src.close()


if __name__ == "__main__":
    main()
