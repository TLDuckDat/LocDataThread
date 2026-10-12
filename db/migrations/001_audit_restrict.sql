-- 001: moderation_audits.comment_id đổi từ ON DELETE CASCADE sang ON DELETE RESTRICT.
-- Lý do: nhật ký kiểm duyệt không được biến mất âm thầm khi ai đó xóa bình luận.
-- Idempotent: chạy lại nhiều lần an toàn (không làm gì nếu FK đã là RESTRICT).
--
-- Chạy: psql -h localhost -U postgres -d <tên_database> -v ON_ERROR_STOP=1 -f db/migrations/001_audit_restrict.sql

DO $$
DECLARE
    fk record;
BEGIN
    FOR fk IN
        SELECT conname
        FROM pg_constraint
        WHERE conrelid = 'moderation_audits'::regclass
          AND confrelid = 'comments'::regclass
          AND contype = 'f'
          AND confdeltype <> 'r'          -- 'r' = RESTRICT
    LOOP
        EXECUTE format('ALTER TABLE moderation_audits DROP CONSTRAINT %I', fk.conname);
        ALTER TABLE moderation_audits
            ADD CONSTRAINT moderation_audits_comment_id_fkey
            FOREIGN KEY (comment_id) REFERENCES comments(id) ON DELETE RESTRICT;
    END LOOP;
END
$$;
