-- 005: ràng buộc chuỗi ngữ cảnh F0..F3 (dữ liệu để train phải liên kết đúng).
--   reply_level = 0 : F0 = nội dung (bình luận gốc), F1..F3 rỗng
--   reply_level = 1 : F0 là bình luận gốc (không rỗng), F1 = nội dung, F2..F3 rỗng
--   reply_level = 2 : F0, F1 không rỗng, F2 = nội dung, F3 rỗng
--   reply_level = 3 : F0..F2 không rỗng, F3 = nội dung
-- Nghĩa là: F1 phải có F0, F2 phải có F1, F3 phải có F2 ... và F_L luôn là chính bình luận.
-- Bài viết (review_queue.record_type = 'post') không thuộc quy tắc này.
-- Idempotent: chạy lại nhiều lần an toàn. Chạy sau khi dữ liệu đã đúng (xem tools/check_chain.py).
--
-- Chạy: powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/migrations/005_chain_integrity.sql

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'comments_chain_ck') THEN
        ALTER TABLE comments ADD CONSTRAINT comments_chain_ck CHECK (
            CASE reply_level
                WHEN 0 THEN f0 = content AND f1 = '' AND f2 = '' AND f3 = ''
                WHEN 1 THEN f0 <> '' AND f1 = content AND f2 = '' AND f3 = ''
                WHEN 2 THEN f0 <> '' AND f1 <> '' AND f2 = content AND f3 = ''
                WHEN 3 THEN f0 <> '' AND f1 <> '' AND f2 <> '' AND f3 = content
                ELSE FALSE
            END);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'review_queue_chain_ck') THEN
        ALTER TABLE review_queue ADD CONSTRAINT review_queue_chain_ck CHECK (
            record_type = 'post' OR CASE reply_level
                WHEN 0 THEN f0 = content AND f1 = '' AND f2 = '' AND f3 = ''
                WHEN 1 THEN f0 <> '' AND f1 = content AND f2 = '' AND f3 = ''
                WHEN 2 THEN f0 <> '' AND f1 <> '' AND f2 = content AND f3 = ''
                WHEN 3 THEN f0 <> '' AND f1 <> '' AND f2 <> '' AND f3 = content
                ELSE FALSE
            END);
    END IF;
END
$$;
