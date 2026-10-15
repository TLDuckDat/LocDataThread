"""Ghi ngược quyết định trong Kho phụ về threads_review.db của crawler.

Chạy (TẮT crawler trước: SQLite chỉ cho một tiến trình ghi tại một thời điểm):
    python tools/sync_review_back.py --crawler "<đường dẫn>/crawlDataThreads/crawlDataThreads" --dry-run
    python tools/sync_review_back.py --crawler "<đường dẫn>/crawlDataThreads/crawlDataThreads"

- Chỉ đẩy các quyết định đã đưa ra (promoted/rejected) mà chưa từng ghi ngược (synced_at IS NULL).
- Chỉ đổi bản ghi đang 'pending' bên crawler; không đụng bản ghi crawler đã xử lý khác.
- Đã đưa vào CSDL chính: crawler chỉ đổi trạng thái, không tự ghi bình luận vào SQLite của nó
  (CSDL chính là PostgreSQL). Lần cào sau, bản ghi không còn chờ nữa nên không quay lại Kho phụ.
- Chạy lại an toàn: bản đã ghi (synced_at đã có) được bỏ qua.
"""
import argparse
import sqlite3
from datetime import datetime
from pathlib import Path

import psycopg2

from db_env import default_dsn


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--crawler", required=True, help="Thư mục gốc crawler (chứa data/threads_review.db)")
    parser.add_argument("--review-db", help="Đường dẫn threads_review.db (mặc định: <crawler>/data/threads_review.db)")
    parser.add_argument("--dsn", help="Chuỗi kết nối PostgreSQL (mặc định: Supabase trong .env)")
    parser.add_argument("--dry-run", action="store_true", help="Chỉ đếm, không ghi gì")
    args = parser.parse_args()
    args.dsn = args.dsn or default_dsn()

    review_db = Path(args.review_db) if args.review_db else Path(args.crawler) / "data" / "threads_review.db"
    if not review_db.exists():
        raise SystemExit(f"Không tìm thấy {review_db}")

    dst = psycopg2.connect(args.dsn)
    try:
        with dst, dst.cursor() as pg:
            pg.execute("""
                SELECT id, decision, decided_by, decided_at, crawler_ids
                FROM review_queue
                WHERE decision IN ('promoted', 'rejected') AND synced_at IS NULL
                ORDER BY decided_at
            """)
            decisions = pg.fetchall()

            # Một bình luận có thể có nhiều bản ghi bên crawler (cùng nội dung, khác lý do)
            updates = []
            for row_id, decision, decided_by, decided_at, crawler_ids in decisions:
                stamp = (decided_at or datetime.now().astimezone()).isoformat()
                note = f"Quyết định trong ThreadsToxic bởi {decided_by or 'ẩn danh'}"
                for crawler_id in crawler_ids or []:
                    updates.append((decision, note, stamp, crawler_id, row_id))

            print(f"Quyết định chưa ghi ngược: {len(decisions)} bình luận/bài viết, {len(updates)} bản ghi bên crawler.")
            if args.dry_run or not updates:
                if args.dry_run:
                    print("(dry-run: không ghi gì)")
                return

            src = sqlite3.connect(review_db, timeout=30.0)
            try:
                with src:
                    changed = 0
                    for decision, note, stamp, crawler_id, _ in updates:
                        cur = src.execute(
                            "UPDATE review_records SET status = ?, review_note = ?, reviewed_at = ? "
                            "WHERE id = ? AND status = 'pending'",
                            (decision, note, stamp, crawler_id),
                        )
                        changed += cur.rowcount
            finally:
                src.close()

            # Đánh dấu đã ghi ngược sau khi SQLite đã commit
            synced_ids = sorted({row_id for *_, row_id in updates})
            pg.execute("UPDATE review_queue SET synced_at = NOW() WHERE id = ANY(%s)", (synced_ids,))
            print(f"Đã ghi ngược: {changed} bản ghi bên crawler (các bản còn lại đã không ở trạng thái chờ).")
    finally:
        dst.close()


if __name__ == "__main__":
    main()
