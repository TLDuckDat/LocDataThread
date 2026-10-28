-- 007: liên kết cha-con trong luồng hội thoại và ràng buộc "cha bị loại thì con cũng bị loại".
--
-- Vì sao: parent_comment_id từ crawler thiếu ở ~96% bản trả lời, nên không dùng được. Cha của một bản trả lời
-- cấp L được xác định bằng chuỗi F: cùng bài viết, cấp L-1, nội dung cha = f(L-1), và cùng F0..F(L-2).
-- Trên dữ liệu thật (10/2026) mọi bản trả lời khớp đúng một cha, không nhập nhằng.
--
--   comments.thread_parent_id  cha trực tiếp (NULL với F0)
--   comments.thread_root_id    bình luận gốc F0 của luồng (F0 trỏ về chính nó)
--   moderation_audits.cause_id bình luận gây ra thao tác lan (exclude_cascade): con bị loại theo cha
--
-- Ràng buộc (kiểm tra trong DB, mọi đường ghi đều phải tuân theo: API, script nạp, psql):
--   R1  mỗi bản trả lời có đúng một cha hợp lệ (trigger comments_link_thread + CHECK + khóa ngoại)
--   R2  bình luận đang lọc thì cha không được ở "Đã loại" (constraint trigger, kiểm tra lúc commit)
--   R4  bình luận mới có cha đã bị loại thì vào thẳng "Đã loại" (người thao tác 'system', audit exclude_cascade)
--
-- Idempotent. Dừng (không đổi gì) nếu còn bản trả lời không xác định được đúng một cha.
--
-- Chạy: psql -h localhost -U postgres -d <tên_database> -v ON_ERROR_STOP=1 -f db/migrations/007_thread_links.sql

ALTER TABLE comments ADD COLUMN IF NOT EXISTS thread_parent_id VARCHAR(100);
ALTER TABLE comments ADD COLUMN IF NOT EXISTS thread_root_id   VARCHAR(100);
ALTER TABLE moderation_audits ADD COLUMN IF NOT EXISTS cause_id VARCHAR(100);

CREATE INDEX IF NOT EXISTS idx_comments_post_level    ON comments (post_id, reply_level);
CREATE INDEX IF NOT EXISTS idx_comments_thread_parent ON comments (thread_parent_id) WHERE thread_parent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_comments_thread_root   ON comments (thread_root_id);
CREATE INDEX IF NOT EXISTS idx_audits_cause           ON moderation_audits (cause_id) WHERE cause_id IS NOT NULL;

-- Cha của một bản trả lời theo chuỗi F; NULL nếu không có hoặc có nhiều hơn một ứng viên.
CREATE OR REPLACE FUNCTION find_thread_parent(p_post_id TEXT, p_level INT, p_f0 TEXT, p_f1 TEXT, p_f2 TEXT)
RETURNS VARCHAR
LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN COUNT(*) = 1 THEN MIN(p.id) END
    FROM comments p
    WHERE p_level BETWEEN 1 AND 3
      AND p.post_id = p_post_id
      AND p.reply_level = p_level - 1
      AND p.content = CASE p_level WHEN 1 THEN p_f0 WHEN 2 THEN p_f1 ELSE p_f2 END
      AND (p_level < 2 OR p.f0 = p_f0)
      AND (p_level < 3 OR p.f1 = p_f1)
$$;

-- Điền liên kết cho dữ liệu đã có, từng cấp một (cấp L cần thread_root_id của cấp L-1)
DO $$
DECLARE
    unresolved INT;
BEGIN
    UPDATE comments SET thread_root_id = id
    WHERE reply_level = 0 AND thread_root_id IS DISTINCT FROM id;

    FOR lvl IN 1..3 LOOP
        UPDATE comments c
        SET thread_parent_id = find_thread_parent(c.post_id, c.reply_level, c.f0, c.f1, c.f2)
        WHERE c.reply_level = lvl AND c.thread_parent_id IS NULL;

        UPDATE comments c
        SET thread_root_id = p.thread_root_id
        FROM comments p
        WHERE c.reply_level = lvl AND p.id = c.thread_parent_id
          AND c.thread_root_id IS DISTINCT FROM p.thread_root_id;
    END LOOP;

    SELECT COUNT(*) INTO unresolved FROM comments WHERE reply_level > 0 AND thread_parent_id IS NULL;
    IF unresolved > 0 THEN
        RAISE EXCEPTION '007: % bản trả lời không xác định được đúng một bình luận cha. Kiểm tra bằng db/checks/chain_integrity.sql trước khi chạy lại.', unresolved;
    END IF;

    -- Sửa vi phạm R2 có từ trước: con đang lọc dưới cha đã loại thì bị loại theo (người thao tác 'system').
    -- Đi từ cấp 1 xuống cấp 3 để lan hết các cấp; cause_id là cha trực tiếp.
    FOR lvl IN 1..3 LOOP
        WITH fixed AS (
            UPDATE comments c
            SET is_excluded = TRUE, excluded_by = 'system', excluded_at = NOW()
            FROM comments p
            WHERE c.reply_level = lvl AND p.id = c.thread_parent_id AND p.is_excluded AND NOT c.is_excluded
            RETURNING c.id, c.thread_parent_id
        )
        INSERT INTO moderation_audits (comment_id, user_name, action, cause_id)
        SELECT id, 'system', 'exclude_cascade', thread_parent_id FROM fixed;
    END LOOP;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'comments_thread_parent_fk') THEN
        ALTER TABLE comments ADD CONSTRAINT comments_thread_parent_fk
            FOREIGN KEY (thread_parent_id) REFERENCES comments(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'comments_thread_link_ck') THEN
        ALTER TABLE comments ADD CONSTRAINT comments_thread_link_ck CHECK (
            (reply_level = 0 AND thread_parent_id IS NULL AND thread_root_id = id)
            OR (reply_level > 0 AND thread_parent_id IS NOT NULL AND thread_root_id IS NOT NULL));
    END IF;
END
$$;

-- R1 + R4: tự gắn cha khi thêm hoặc đổi chuỗi F; kiểm tra cha khớp; con của cha đã loại vào thẳng "Đã loại".
-- Lưu ý khi chèn nhiều dòng trong một câu lệnh: cha phải được chèn ở câu lệnh trước (nạp theo từng cấp).
CREATE OR REPLACE FUNCTION comments_link_thread() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    parent comments%ROWTYPE;
BEGIN
    IF NEW.reply_level = 0 THEN
        NEW.thread_parent_id := NULL;
        NEW.thread_root_id := NEW.id;
        RETURN NEW;
    END IF;

    IF NEW.thread_parent_id IS NULL OR TG_OP = 'UPDATE' THEN
        NEW.thread_parent_id := find_thread_parent(NEW.post_id, NEW.reply_level, NEW.f0, NEW.f1, NEW.f2);
    END IF;

    SELECT * INTO parent FROM comments WHERE id = NEW.thread_parent_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Bình luận % (cấp F%) không có đúng một bình luận cha trong cùng bài viết', NEW.id, NEW.reply_level
            USING ERRCODE = 'foreign_key_violation', CONSTRAINT = 'comments_thread_parent_fk';
    END IF;

    IF parent.post_id <> NEW.post_id OR parent.reply_level <> NEW.reply_level - 1 THEN
        RAISE EXCEPTION 'Bình luận cha % không khớp với % (khác bài viết hoặc sai cấp)', parent.id, NEW.id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'comments_thread_link_ck';
    END IF;

    NEW.thread_root_id := parent.thread_root_id;

    IF TG_OP = 'INSERT' AND parent.is_excluded AND NOT NEW.is_excluded THEN
        NEW.is_excluded := TRUE;
        NEW.excluded_by := 'system';
        NEW.excluded_at := NOW();
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS comments_link_thread ON comments;
CREATE TRIGGER comments_link_thread
    BEFORE INSERT OR UPDATE OF reply_level, post_id, f0, f1, f2 ON comments
    FOR EACH ROW EXECUTE FUNCTION comments_link_thread();

-- Ghi audit cho bình luận bị loại theo cha ngay lúc chèn (R4)
CREATE OR REPLACE FUNCTION comments_audit_inherited_exclusion() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO moderation_audits (comment_id, user_name, action, cause_id)
    VALUES (NEW.id, 'system', 'exclude_cascade', NEW.thread_parent_id);
    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS comments_audit_inherited_exclusion ON comments;
CREATE TRIGGER comments_audit_inherited_exclusion
    AFTER INSERT ON comments
    FOR EACH ROW
    WHEN (NEW.is_excluded AND NEW.thread_parent_id IS NOT NULL AND NEW.excluded_by = 'system')
    EXECUTE FUNCTION comments_audit_inherited_exclusion();

-- R2: kiểm tra lúc commit (DEFERRABLE), nên trong một transaction có thể đổi cha và con theo thứ tự bất kỳ.
-- Đọc trạng thái hiện tại của dòng (không dùng NEW) vì dòng có thể đã đổi tiếp sau sự kiện này.
CREATE OR REPLACE FUNCTION comments_exclusion_chain_check() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    cur comments%ROWTYPE;
BEGIN
    SELECT * INTO cur FROM comments WHERE id = NEW.id;
    IF NOT FOUND THEN
        RETURN NULL;
    END IF;

    IF NOT cur.is_excluded AND cur.thread_parent_id IS NOT NULL
       AND EXISTS (SELECT 1 FROM comments p WHERE p.id = cur.thread_parent_id AND p.is_excluded) THEN
        RAISE EXCEPTION 'Bình luận % đang lọc nhưng bình luận cha % đã bị loại', cur.id, cur.thread_parent_id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'comments_exclusion_chain';
    END IF;

    IF cur.is_excluded
       AND EXISTS (SELECT 1 FROM comments c WHERE c.thread_parent_id = cur.id AND NOT c.is_excluded) THEN
        RAISE EXCEPTION 'Bình luận % đã bị loại nhưng còn bình luận con đang lọc', cur.id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'comments_exclusion_chain';
    END IF;

    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS comments_exclusion_chain ON comments;
CREATE CONSTRAINT TRIGGER comments_exclusion_chain
    AFTER INSERT OR UPDATE OF is_excluded, thread_parent_id ON comments
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION comments_exclusion_chain_check();
