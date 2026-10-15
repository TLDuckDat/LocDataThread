-- Schema cho ThreadsToxic (MVP: bình luận + lọc cộng tác)
-- Chạy: powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/schema.sql

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS comments (
    id                  VARCHAR(100) PRIMARY KEY,
    post_id             VARCHAR(100) NOT NULL,
    post_url            TEXT NOT NULL,
    comment_url         TEXT,
    author_username     VARCHAR(200),
    author_name         VARCHAR(300),
    content             TEXT NOT NULL,
    posted_at           TEXT,                       -- giữ nguyên chuỗi gốc từ crawler
    likes               INT NOT NULL DEFAULT 0,
    reply_level         INT NOT NULL DEFAULT 0,     -- 0: F0, 1: F1, 2: F2, 3: F3
    parent_comment_id   VARCHAR(100),
    f0                  TEXT NOT NULL DEFAULT '',
    f1                  TEXT NOT NULL DEFAULT '',
    f2                  TEXT NOT NULL DEFAULT '',
    f3                  TEXT NOT NULL DEFAULT '',
    toxic_score         REAL NOT NULL DEFAULT 0,
    severity            VARCHAR(60),
    -- Dữ liệu phi quan hệ (JSONB) từ bộ phát hiện tự động
    analysis_data       JSONB NOT NULL DEFAULT '{}'::jsonb,

    -- Trạng thái lọc chung: false = đang trong danh sách lọc, true = đã loại (có thể khôi phục)
    is_excluded         BOOLEAN NOT NULL DEFAULT FALSE,
    excluded_by         VARCHAR(60),
    excluded_at         TIMESTAMPTZ,

    scraped_at          TEXT,

    -- Nguồn: NULL = dữ liệu chính; khác NULL = nạp từ hàng chờ lọc trùng, giá trị là lý do bị cách ly
    source              VARCHAR(40),

    -- Phục vụ xuất Excel/CSV giống crawler (xem db/migrations/003_export_fields.sql)
    is_reply            BOOLEAN NOT NULL DEFAULT FALSE,
    review_status       VARCHAR(20),                -- 'bad' | 'ambiguous' | 'clean'
    review_status_vi    VARCHAR(100),
    user_review         VARCHAR(20),
    user_review_vi      VARCHAR(100),
    user_keywords       TEXT,                       -- chuỗi JSON gốc
    source_rowid        BIGINT,                     -- rowid bên SQLite: thứ tự cây hội thoại

    -- Người xử lý gần nhất (duyệt, loại, khôi phục, hoàn tác); xem db/migrations/004_review_queue.sql
    reviewed_by         VARCHAR(60),
    reviewed_at         TIMESTAMPTZ
);

-- Bài viết gốc (nội dung + chủ đề) cho file xuất
CREATE TABLE IF NOT EXISTS posts (
    id                  VARCHAR(100) PRIMARY KEY,
    url                 TEXT,
    author_username     VARCHAR(200),
    author_name         VARCHAR(300),
    content             TEXT,
    categories          TEXT                        -- chuỗi JSON gốc
);

CREATE TABLE IF NOT EXISTS moderation_audits (
    id          BIGSERIAL PRIMARY KEY,
    comment_id  VARCHAR(100) NOT NULL REFERENCES comments(id) ON DELETE RESTRICT,
    user_name   VARCHAR(60) NOT NULL,
    action      VARCHAR(30) NOT NULL,              -- 'exclude' | 'restore'
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Lọc nhanh theo bucket + điểm
CREATE INDEX IF NOT EXISTS idx_comments_excluded_score ON comments (is_excluded, toxic_score DESC);
CREATE INDEX IF NOT EXISTS idx_comments_reply_level    ON comments (reply_level);
-- Lọc theo từ khóa/emoji trong JSONB
CREATE INDEX IF NOT EXISTS idx_comments_analysis_gin   ON comments USING GIN (analysis_data);
-- Tìm kiếm nội dung
CREATE INDEX IF NOT EXISTS idx_comments_content_trgm   ON comments USING GIN (content gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_comments_source        ON comments (source) WHERE source IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_audits_comment          ON moderation_audits (comment_id);

-- Kho phụ: bản ghi từ threads_review.db, chưa vào "Đang lọc" (xem db/migrations/004_review_queue.sql)
CREATE TABLE IF NOT EXISTS review_queue (
    id                  VARCHAR(100) PRIMARY KEY,
    record_type         VARCHAR(10) NOT NULL CHECK (record_type IN ('comment', 'post')),
    reason              VARCHAR(40) NOT NULL,
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
    categories          TEXT,
    occurrences         INT NOT NULL DEFAULT 1,
    first_seen          TEXT,
    last_seen           TEXT,
    exists_in_main      BOOLEAN NOT NULL DEFAULT FALSE,
    decision            VARCHAR(20) NOT NULL DEFAULT 'pending'
                        CHECK (decision IN ('pending', 'promoted', 'rejected')),
    decided_by          VARCHAR(60),
    decided_at          TIMESTAMPTZ,
    synced_at           TIMESTAMPTZ,
    crawler_ids         TEXT[] NOT NULL DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS idx_review_queue_pending ON review_queue (decision, exists_in_main, reason);
CREATE INDEX IF NOT EXISTS idx_review_queue_last_seen ON review_queue (last_seen DESC);
CREATE INDEX IF NOT EXISTS idx_review_queue_unsynced ON review_queue (decision) WHERE synced_at IS NULL AND decision <> 'pending';
CREATE INDEX IF NOT EXISTS idx_review_queue_content_trgm ON review_queue USING GIN (content gin_trgm_ops);
