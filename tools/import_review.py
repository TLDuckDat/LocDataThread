"""Nạp hàng chờ lọc trùng (threads_review.db) vào Kho phụ (bảng review_queue) của PostgreSQL.

Chạy:
    python tools/import_review.py --crawler "<đường dẫn>/crawlDataThreads/crawlDataThreads"
    python tools/import_review.py --crawler "..." --reason non_vietnamese --dry-run

- Chỉ ĐỌC threads_review.db (mở ở chế độ read-only), không đổi trạng thái bản ghi bên crawler.
- Bản ghi chỉ vào Kho phụ, KHÔNG vào "Đang lọc"; người dùng đưa sang bằng giao diện (xem README).
- Bản ghi đã có cùng id trong dữ liệu chính được đánh dấu exists_in_main và tự ẩn khỏi Kho phụ.
- Chạy lại an toàn: quyết định đã có (promoted/rejected) và người quyết định không bị ghi đè;
  chỉ cập nhật số lần gặp và thời điểm gặp gần nhất.
- Điểm độc hại được tính lại bằng toxic_engine của crawler, cùng thang với dữ liệu chính.
"""
import argparse
import json
import sqlite3
import sys
from pathlib import Path

import psycopg2
from psycopg2.extras import execute_values

from db_env import default_dsn

BATCH_SIZE = 1000


QUEUE_COLUMNS = (
    "id", "record_type", "reason", "record_id", "post_id", "post_url", "comment_url",
    "author_username", "author_name", "content", "posted_at", "likes", "reply_level",
    "is_reply", "parent_comment_id", "f0", "f1", "f2", "f3", "toxic_score", "severity",
    "review_status", "review_status_vi", "analysis_data", "categories", "occurrences",
    "first_seen", "last_seen", "crawler_ids",
)

# Chỉ cập nhật số liệu quan sát và danh sách id bên crawler;
# decision/decided_by/decided_at/synced_at không bao giờ bị ghi đè.
INSERT_SQL = f"""
    INSERT INTO review_queue ({", ".join(QUEUE_COLUMNS)})
    VALUES %s
    ON CONFLICT (id) DO UPDATE SET
        occurrences = EXCLUDED.occurrences,
        last_seen = EXCLUDED.last_seen,
        crawler_ids = EXCLUDED.crawler_ids
"""

MARK_IN_MAIN_SQL = """
    UPDATE review_queue q
    SET exists_in_main = CASE
        WHEN q.record_type = 'comment' THEN EXISTS (SELECT 1 FROM comments c WHERE c.id = q.id)
        ELSE EXISTS (SELECT 1 FROM posts p WHERE p.id = q.id)
    END
    WHERE q.exists_in_main IS DISTINCT FROM (CASE
        WHEN q.record_type = 'comment' THEN EXISTS (SELECT 1 FROM comments c WHERE c.id = q.id)
        ELSE EXISTS (SELECT 1 FROM posts p WHERE p.id = q.id)
    END)
"""


INCOMPLETE_SQL = """
    INSERT INTO review_incomplete (id, reason, post_id, post_url, comment_url, author_username, author_name,
        content, reply_to, reply_level, crawler_ids, first_seen, last_seen)
    VALUES %s
    ON CONFLICT (id) DO UPDATE SET last_seen = EXCLUDED.last_seen, crawler_ids = EXCLUDED.crawler_ids
"""


def load_analyzer(crawler_root):
    """Dùng lại toxic_engine của crawler để điểm số khớp với dữ liệu chính."""
    sys.path.insert(0, str(crawler_root))
    from src.detector.toxic_engine import toxic_engine
    return toxic_engine.analyze


def parse_payload(raw_json):
    try:
        payload = json.loads(raw_json)
    except (TypeError, json.JSONDecodeError):
        return {}, {}
    if not isinstance(payload, dict):
        return {}, {}
    if payload.get("model_type") in ("comment", "post"):
        return payload.get("model") or {}, {}
    raw = payload.get("raw_item", payload)
    context = payload.get("context") or {}
    return (raw if isinstance(raw, dict) else {}), (context if isinstance(context, dict) else {})


def record_id_of(raw, context, record):
    return str(context.get("record_id") or raw.get("id") or record["record_id"] or "")[:100]


def comment_row(record, raw, context, analyze):
    """Bản ghi bình luận -> dòng của review_queue. None nếu thiếu id, nội dung hoặc link bài viết."""
    comment_id = record_id_of(raw, context, record)
    content = str(raw.get("content") or record["content"] or "")
    post_url = context.get("post_url") or raw.get("post_url") or record["post_url"] or record["source_url"]
    if not comment_id or not content.strip() or not post_url:
        return None

    is_reply = bool(raw.get("isReply") or raw.get("replyTo") or raw.get("is_reply"))
    level = context.get("reply_level", raw.get("reply_level", 1 if is_reply else 0))
    try:
        level = max(0, min(3, int(level or 0)))
    except (TypeError, ValueError):
        level = 0

    result = analyze(content)
    analysis = {
        "matched_words": list(result.matched_words),
        "matched_emojis": list(result.matched_emojis),
        "categories": list(result.category_names),
        "has_emoji_slang": bool(result.has_emoji_slang),
        "is_auto_toxic": bool(result.is_toxic),
    }
    # Chuỗi ngữ cảnh: F_L luôn là chính bình luận; F0..F(L-1) là các bình luận cha. Thiếu cha thì bỏ qua,
    # vì F1 phải có F0, F2 phải có F1... (dữ liệu chính đã được kiểm tra theo đúng quy tắc này)
    chain = [context.get(f"f{i}", raw.get(f"f{i}", "")) or "" for i in range(4)]
    chain[level] = content
    if any(not chain[k] for k in range(level)):
        # Không dựng được chuỗi cha: để người xử lý sau (review_incomplete), không đưa vào hàng chờ
        return {"_incomplete": True, "id": comment_id, "reason": str(record["reason"])[:40],
                "post_id": str(context.get("post_id") or raw.get("post_id") or "")[:100],
                "post_url": post_url, "comment_url": raw.get("itemUrl") or None,
                "author_username": raw.get("username") or record["author_username"] or None,
                "author_name": raw.get("authorName") or record["author_name"] or None,
                "content": content, "reply_to": raw.get("replyTo") or None, "reply_level": level,
                "first_seen": record["first_seen"] or None, "last_seen": record["last_seen"] or None}

    try:
        likes = int(raw.get("likes") or 0)
    except (TypeError, ValueError):
        likes = 0

    return {
        "id": comment_id,
        "record_type": "comment",
        "reason": str(record["reason"])[:40],
        "record_id": comment_id,
        "post_id": str(context.get("post_id") or raw.get("post_id") or "")[:100],
        "post_url": post_url,
        "comment_url": raw.get("itemUrl") or raw.get("comment_url") or None,
        "author_username": raw.get("username") or raw.get("author_username") or record["author_username"] or None,
        "author_name": raw.get("authorName") or raw.get("author_name") or record["author_name"] or None,
        "content": content,
        "posted_at": raw.get("timeStr") or raw.get("posted_at") or None,
        "likes": likes,
        "reply_level": level,
        "is_reply": level > 0,
        "parent_comment_id": context.get("parent_comment_id") or raw.get("parent_comment_id") or None,
        "f0": chain[0], "f1": chain[1], "f2": chain[2], "f3": chain[3],
        "toxic_score": float(result.score),
        "severity": result.severity_vi or None,
        "review_status": result.review_status,
        "review_status_vi": result.review_status_vi,
        "analysis_data": json.dumps(analysis, ensure_ascii=False),
        "categories": None,
        "occurrences": int(record["occurrences"] or 1),
        "first_seen": record["first_seen"] or None,
        "last_seen": record["last_seen"] or None,
    }


def post_row(record, raw, context, analyze):
    """Bản ghi bài viết -> dòng của review_queue. None nếu thiếu id hoặc nội dung."""
    post_id = record_id_of(raw, context, record)
    content = str(raw.get("content") or record["content"] or "")
    if not post_id or not content.strip():
        return None
    url = context.get("post_url") or raw.get("url") or record["source_url"] or ""
    result = analyze(content)
    analysis = {
        "matched_words": list(result.matched_words),
        "matched_emojis": list(result.matched_emojis),
        "categories": list(result.category_names),
        "has_emoji_slang": bool(result.has_emoji_slang),
        "is_auto_toxic": bool(result.is_toxic),
    }
    return {
        "id": post_id,
        "record_type": "post",
        "reason": str(record["reason"])[:40],
        "record_id": post_id,
        "post_id": post_id,
        "post_url": str(url)[:2000],
        "comment_url": None,
        "author_username": raw.get("username") or raw.get("author_username") or record["author_username"] or None,
        "author_name": raw.get("authorName") or raw.get("author_name") or record["author_name"] or None,
        "content": content,
        "posted_at": raw.get("timeStr") or raw.get("posted_at") or None,
        "likes": int(raw.get("likes") or 0) if str(raw.get("likes") or 0).isdigit() else 0,
        "reply_level": 0,
        "is_reply": False,
        "parent_comment_id": None,
        "f0": content, "f1": "", "f2": "", "f3": "",
        "toxic_score": float(result.score),
        "severity": result.severity_vi or None,
        "review_status": result.review_status,
        "review_status_vi": result.review_status_vi,
        "analysis_data": json.dumps(analysis, ensure_ascii=False),
        "categories": json.dumps(list(result.category_names), ensure_ascii=False),
        "occurrences": int(record["occurrences"] or 1),
        "first_seen": record["first_seen"] or None,
        "last_seen": record["last_seen"] or None,
    }


def to_tuple(row):
    return tuple(row[c] for c in QUEUE_COLUMNS)


def record_key(record):
    """Khóa một bản ghi trong Kho phụ: loại + id bình luận/bài viết (một dòng cho mỗi khóa)."""
    raw, context = parse_payload(record["payload_json"])
    kind = "post" if record["record_type"] == "post" else "comment"
    return kind, record_id_of(raw, context, record)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--crawler", required=True,
                        help="Thư mục gốc crawler (chứa data/threads_review.db và src/detector)")
    parser.add_argument("--review-db", help="Đường dẫn threads_review.db (mặc định: <crawler>/data/threads_review.db)")
    parser.add_argument("--reason", action="append",
                        help="Chỉ nạp lý do này (lặp lại được), vd: duplicate_content, non_vietnamese")
    parser.add_argument("--dsn", help="Chuỗi kết nối PostgreSQL (mặc định: Supabase trong .env)")
    parser.add_argument("--dry-run", action="store_true", help="Chỉ đếm, không ghi vào PostgreSQL")
    args = parser.parse_args()
    args.dsn = args.dsn or default_dsn()

    crawler_root = Path(args.crawler).resolve()
    review_db = Path(args.review_db) if args.review_db else crawler_root / "data" / "threads_review.db"
    if not review_db.exists():
        raise SystemExit(f"Không tìm thấy {review_db}")

    analyze = load_analyzer(crawler_root)

    src = sqlite3.connect(f"file:{review_db.as_posix()}?mode=ro", uri=True)
    src.row_factory = sqlite3.Row
    query = "SELECT * FROM review_records WHERE status = 'pending'"
    params = []
    if args.reason:
        query += f" AND reason IN ({','.join('?' for _ in args.reason)})"
        params.extend(args.reason)
    query += " ORDER BY last_seen DESC"

    # Lượt 1: gom id hàng chờ của crawler theo từng bình luận (để ghi ngược đúng các bản ghi)
    crawler_ids = {}
    for record in src.execute(query, params):
        key = record_key(record)
        if key[1]:
            crawler_ids.setdefault(key, []).append(record["id"])

    dst = psycopg2.connect(args.dsn)
    seen, skipped_invalid, inserted, by_reason = set(), 0, 0, {}
    incomplete = []
    with psycopg2.connect(args.dsn) as probe, probe.cursor() as cur:
        cur.execute("SELECT id FROM comments UNION ALL SELECT id FROM posts")
        main_ids = {row[0] for row in cur.fetchall()}
    try:
        with dst, dst.cursor() as pg:
            batch = []

            def flush():
                nonlocal inserted
                if not batch:
                    return
                if not args.dry_run:
                    execute_values(pg, INSERT_SQL, batch)
                inserted += len(batch)
                batch.clear()
                print(f"  đã xử lý {inserted} bản ghi...", flush=True)

            for record in src.execute(query, params):
                raw, context = parse_payload(record["payload_json"])
                kind = "post" if record["record_type"] == "post" else "comment"
                key = record_key(record)
                if not key[1] or key in seen:
                    continue
                seen.add(key)
                row = post_row(record, raw, context, analyze) if kind == "post" \
                    else comment_row(record, raw, context, analyze)
                if row is None:
                    skipped_invalid += 1
                    continue
                if row.get("_incomplete"):
                    # Đã có trong dữ liệu chính với chuỗi đầy đủ: không cần lưu lại
                    if row["id"] in main_ids:
                        continue
                    row["crawler_ids"] = crawler_ids.get(key, [record["id"]])
                    incomplete.append(row)
                    continue
                row["crawler_ids"] = crawler_ids.get(key, [record["id"]])
                by_reason[(kind, row["reason"])] = by_reason.get((kind, row["reason"]), 0) + 1
                batch.append(to_tuple(row))
                if len(batch) >= BATCH_SIZE:
                    flush()
            flush()

            if args.dry_run:
                dst.rollback()
            else:
                if incomplete:
                    execute_values(pg, INCOMPLETE_SQL, [
                        (r["id"], r["reason"], r["post_id"], r["post_url"], r["comment_url"],
                         r["author_username"], r["author_name"], r["content"], r["reply_to"],
                         r["reply_level"], r["crawler_ids"], r["first_seen"], r["last_seen"])
                        for r in incomplete])
                pg.execute(MARK_IN_MAIN_SQL)
                pg.execute("SELECT COUNT(*), COUNT(*) FILTER (WHERE exists_in_main), "
                           "COUNT(*) FILTER (WHERE decision = 'pending') FROM review_queue")
                total, in_main, pending = pg.fetchone()
    finally:
        dst.close()
        src.close()

    verb = "Sẽ nạp" if args.dry_run else "Đã nạp"
    print(f"Xong. {verb} {inserted} bản ghi vào Kho phụ.")
    for (kind, reason), count in sorted(by_reason.items(), key=lambda kv: -kv[1]):
        print(f"  - {kind} · {reason}: {count}")
    if skipped_invalid:
        print(f"Bỏ qua {skipped_invalid} bản ghi thiếu nội dung/id/link.")
    if incomplete:
        print(f"{len(incomplete)} bản trả lời thiếu bình luận cha: lưu vào review_incomplete để xử lý sau (không đưa vào hàng chờ).")
    if not args.dry_run:
        print(f"Kho phụ hiện có {total} bản ghi: {pending} đang chờ, {in_main} đã có trong dữ liệu chính (tự ẩn).")


if __name__ == "__main__":
    main()
