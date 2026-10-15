-- 002: thêm cột comments.source để biết bình luận đến từ đâu.
--   NULL                 = dữ liệu chính (threads_data.db)
--   'duplicate_content'… = nạp từ hàng chờ lọc trùng (threads_review.db), giá trị là lý do bị cách ly
-- Idempotent: chạy lại nhiều lần an toàn.
--
-- Chạy: powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/migrations/002_comment_source.sql

ALTER TABLE comments ADD COLUMN IF NOT EXISTS source VARCHAR(40);

-- Chỉ đánh chỉ mục phần dữ liệu từ hàng chờ (dữ liệu chính có source NULL chiếm đa số)
CREATE INDEX IF NOT EXISTS idx_comments_source ON comments (source) WHERE source IS NOT NULL;
