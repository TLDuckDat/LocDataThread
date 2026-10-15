-- 006: bản trả lời trong hàng chờ lọc trùng nhưng thiếu ngữ cảnh chuỗi (không có F0..F(L-1)).
-- Không đưa vào review_queue (sẽ vi phạm chuỗi F), mà lưu riêng để không mất dữ liệu và xử lý sau.
-- Nguyên nhân: crawler lưu bản raw trước khi tính chuỗi F0..F3 (xem threads_crawler.py).
-- Idempotent.
--
-- Chạy: powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/migrations/006_review_incomplete.sql

CREATE TABLE IF NOT EXISTS review_incomplete (
    id                  VARCHAR(100) PRIMARY KEY,       -- id bình luận (giống review_queue.id)
    reason              VARCHAR(40) NOT NULL,           -- lý do bị cách ly bên crawler
    post_id             VARCHAR(100) NOT NULL DEFAULT '',
    post_url            TEXT NOT NULL DEFAULT '',
    comment_url         TEXT,
    author_username     VARCHAR(200),
    author_name         VARCHAR(300),
    content             TEXT NOT NULL DEFAULT '',
    reply_to            VARCHAR(200),                   -- tên người được trả lời (từ crawler)
    reply_level         INT NOT NULL DEFAULT 0,
    note                VARCHAR(100) NOT NULL DEFAULT 'missing_parent_context',
    crawler_ids         TEXT[] NOT NULL DEFAULT '{}',
    first_seen          TEXT,
    last_seen           TEXT,
    imported_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
