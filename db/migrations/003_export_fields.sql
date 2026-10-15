-- 003: dữ liệu cần cho chức năng xuất Excel/CSV giống hệt crawler (bảng 4 cột chuẩn).
--   posts                         bài viết gốc: nội dung + chủ đề cho cột "Bài viết", "Chủ đề bài viết"
--   comments.is_reply             lọc "Chỉ bình luận gốc / Chỉ bình luận con"
--   comments.review_status(_vi)   lọc Xấu luôn / Chưa rõ / Trong sạch và cột "Điểm đánh giá"
--   comments.user_review(_vi)     tự đánh giá của người dùng bên crawler
--   comments.user_keywords        từ lóng người dùng bổ sung (chuỗi JSON gốc)
--   comments.source_rowid         rowid bên SQLite của crawler: giữ đúng thứ tự cây hội thoại khi xuất
-- Các cột văn bản giữ NULL đúng như nguồn: cách crawler hiển thị giá trị thiếu ("nan") phụ thuộc vào điều đó.
-- Idempotent: chạy lại nhiều lần an toàn. Sau khi chạy, chạy lại tools/import_comments.py để điền dữ liệu.
--
-- Chạy: powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/migrations/003_export_fields.sql

CREATE TABLE IF NOT EXISTS posts (
    id                  VARCHAR(100) PRIMARY KEY,
    url                 TEXT,
    author_username     VARCHAR(200),
    author_name         VARCHAR(300),
    content             TEXT,
    categories          TEXT                        -- chuỗi JSON gốc từ crawler
);

ALTER TABLE comments ADD COLUMN IF NOT EXISTS is_reply         BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE comments ADD COLUMN IF NOT EXISTS review_status    VARCHAR(20);
ALTER TABLE comments ADD COLUMN IF NOT EXISTS review_status_vi VARCHAR(100);
ALTER TABLE comments ADD COLUMN IF NOT EXISTS user_review      VARCHAR(20);
ALTER TABLE comments ADD COLUMN IF NOT EXISTS user_review_vi   VARCHAR(100);
ALTER TABLE comments ADD COLUMN IF NOT EXISTS user_keywords    TEXT;
ALTER TABLE comments ADD COLUMN IF NOT EXISTS source_rowid     BIGINT;
