-- 008: chia việc cho từng người lọc.
--   comments.assigned_to  người phụ trách (tên trong Auth:Users); NULL = chưa giao
-- Admin chia theo bài viết (cả bài về một người, nên cả luồng F0..F3 cũng về một người) qua API
-- POST /api/admin/assign/split; chuyển phần chưa làm qua POST /api/admin/assign/transfer.
-- Giao việc chỉ để sắp xếp công việc: ai cũng vẫn xử lý được mọi bình luận (kiểm tra chéo).
-- Idempotent.
--
-- Chạy: psql -h localhost -U postgres -d <tên_database> -v ON_ERROR_STOP=1 -f db/migrations/008_assignment.sql

ALTER TABLE comments ADD COLUMN IF NOT EXISTS assigned_to VARCHAR(60);

-- "Phần của tôi · chưa xử lý": lọc theo người + trạng thái
CREATE INDEX IF NOT EXISTS idx_comments_assigned ON comments (assigned_to, is_excluded, reviewed_by);
