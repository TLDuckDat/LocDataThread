-- Kiểm tra toàn vẹn chuỗi hội thoại F0..F3 (chỉ đọc). Mọi dòng "violations" phải bằng 0.
-- Chạy trước khi xuất file, sau khi nạp dữ liệu, hoặc khi nghi ngờ dữ liệu lệch. Cần migration 007.
--
-- Chạy: psql -d <tên_database> -f db/checks/chain_integrity.sql
--       powershell -ExecutionPolicy Bypass -File tools\supabase_psql.ps1 -f db\checks\chain_integrity.sql

SELECT 'R1 bản trả lời chưa gắn cha' AS check_name, COUNT(*) AS violations
FROM comments WHERE reply_level > 0 AND thread_parent_id IS NULL
UNION ALL
SELECT 'R1 cha khác bài viết hoặc sai cấp', COUNT(*)
FROM comments c JOIN comments p ON p.id = c.thread_parent_id
WHERE p.post_id <> c.post_id OR p.reply_level <> c.reply_level - 1
UNION ALL
SELECT 'R1 chuỗi F lệch với nội dung cha', COUNT(*)
FROM comments c JOIN comments p ON p.id = c.thread_parent_id
WHERE p.content <> CASE c.reply_level WHEN 1 THEN c.f0 WHEN 2 THEN c.f1 ELSE c.f2 END
   OR (c.reply_level >= 2 AND p.f0 <> c.f0)
   OR (c.reply_level = 3 AND p.f1 <> c.f1)
UNION ALL
SELECT 'R1 thread_root_id sai', COUNT(*)
FROM comments c LEFT JOIN comments p ON p.id = c.thread_parent_id
WHERE (c.reply_level = 0 AND c.thread_root_id IS DISTINCT FROM c.id)
   OR (c.reply_level > 0 AND c.thread_root_id IS DISTINCT FROM p.thread_root_id)
UNION ALL
SELECT 'R2 con đang lọc nhưng cha đã bị loại', COUNT(*)
FROM comments c JOIN comments p ON p.id = c.thread_parent_id
WHERE NOT c.is_excluded AND p.is_excluded
UNION ALL
SELECT 'is_reply lệch với reply_level', COUNT(*)
FROM comments WHERE is_reply <> (reply_level > 0)
UNION ALL
SELECT 'Hình dạng chuỗi F sai (005)', COUNT(*)
FROM comments WHERE NOT CASE reply_level
    WHEN 0 THEN f0 = content AND f1 = '' AND f2 = '' AND f3 = ''
    WHEN 1 THEN f0 <> '' AND f1 = content AND f2 = '' AND f3 = ''
    WHEN 2 THEN f0 <> '' AND f1 <> '' AND f2 = content AND f3 = ''
    WHEN 3 THEN f0 <> '' AND f1 <> '' AND f2 <> '' AND f3 = content
    ELSE FALSE END;
