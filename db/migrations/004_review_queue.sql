-- 004: Kho phụ (hàng chờ lọc trùng) + ghi nhận người đã lọc.
--   comments.reviewed_by/reviewed_at  người xử lý gần nhất (duyệt, loại, khôi phục, hoàn tác)
--   review_queue                      bản ghi từ threads_review.db, CHƯA vào "Đang lọc"
--
-- Kho phụ tách hẳn khỏi comments: cùng id với dữ liệu chính thì không thể nằm chung bảng.
-- Chỉ dữ liệu đã tính sẵn được lưu (không lưu payload thô của crawler).
-- Idempotent: chạy lại nhiều lần an toàn.
--
-- Chạy: powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/migrations/004_review_queue.sql

ALTER TABLE comments ADD COLUMN IF NOT EXISTS reviewed_by VARCHAR(60);
ALTER TABLE comments ADD COLUMN IF NOT EXISTS reviewed_at TIMESTAMPTZ;
CREATE INDEX IF NOT EXISTS idx_comments_reviewed_by ON comments (reviewed_by) WHERE reviewed_by IS NOT NULL;

CREATE TABLE IF NOT EXISTS review_queue (
    id                  VARCHAR(100) PRIMARY KEY,       -- queue_id bên crawler (sha256)
    record_type         VARCHAR(10) NOT NULL CHECK (record_type IN ('comment', 'post')),
    reason              VARCHAR(40) NOT NULL,           -- duplicate_id | duplicate_content | non_vietnamese | meta_ai ...
    record_id           VARCHAR(100) NOT NULL DEFAULT '',
    post_id             VARCHAR(100) NOT NULL DEFAULT '',
    post_url            TEXT NOT NULL DEFAULT '',
    comment_url         TEXT,
    author_username     VARCHAR(200),
    author_name         VARCHAR(300),
    content             TEXT NOT NULL DEFAULT '',
    posted_at           TEXT,
    likes               INT NOT NULL DEFAULT 0,
    reply_level         INT NOT NULL DEFAULT 0,
    is_reply            BOOLEAN NOT NULL DEFAULT FALSE,
    parent_comment_id   VARCHAR(100),
    f0                  TEXT NOT NULL DEFAULT '',
    f1                  TEXT NOT NULL DEFAULT '',
    f2                  TEXT NOT NULL DEFAULT '',
    f3                  TEXT NOT NULL DEFAULT '',
    toxic_score         REAL NOT NULL DEFAULT 0,
    severity            VARCHAR(60),
    review_status       VARCHAR(20),
    review_status_vi    VARCHAR(100),
    analysis_data       JSONB NOT NULL DEFAULT '{}'::jsonb,
    categories          TEXT,                           -- post: chuỗi JSON gốc
    occurrences         INT NOT NULL DEFAULT 1,
    first_seen          TEXT,
    last_seen           TEXT,
    exists_in_main      BOOLEAN NOT NULL DEFAULT FALSE, -- đã có cùng id trong dữ liệu chính: tự đóng, không đưa vào
    decision            VARCHAR(20) NOT NULL DEFAULT 'pending'
                        CHECK (decision IN ('pending', 'promoted', 'rejected')),
    decided_by          VARCHAR(60),
    decided_at          TIMESTAMPTZ,
    synced_at           TIMESTAMPTZ,                    -- đã ghi ngược về threads_review.db của crawler
    crawler_ids         TEXT[] NOT NULL DEFAULT '{}'    -- id các bản ghi review_records bên crawler của cùng bình luận
);

-- Bảng đã tạo từ bản 004 đầu tiên: thêm cột nếu thiếu
ALTER TABLE review_queue ADD COLUMN IF NOT EXISTS crawler_ids TEXT[] NOT NULL DEFAULT '{}';

CREATE INDEX IF NOT EXISTS idx_review_queue_pending ON review_queue (decision, exists_in_main, reason);
CREATE INDEX IF NOT EXISTS idx_review_queue_last_seen ON review_queue (last_seen DESC);
CREATE INDEX IF NOT EXISTS idx_review_queue_unsynced ON review_queue (decision) WHERE synced_at IS NULL AND decision <> 'pending';
CREATE INDEX IF NOT EXISTS idx_review_queue_content_trgm ON review_queue USING GIN (content gin_trgm_ops);
