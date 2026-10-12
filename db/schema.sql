-- Schema cho ThreadsToxic (MVP: bình luận + lọc cộng tác)
-- Chạy: psql -h localhost -U postgres -d threads_toxic_db -f db/schema.sql

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

    scraped_at          TEXT
);

CREATE TABLE IF NOT EXISTS moderation_audits (
    id          BIGSERIAL PRIMARY KEY,
    comment_id  VARCHAR(100) NOT NULL REFERENCES comments(id) ON DELETE CASCADE,
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
CREATE INDEX IF NOT EXISTS idx_audits_comment          ON moderation_audits (comment_id);
