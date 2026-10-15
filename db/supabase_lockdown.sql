-- Chỉ dành cho Supabase: chặn Data API (role anon/authenticated, tức publishable key) khỏi bảng của app.
-- App kết nối bằng chủ sở hữu bảng (postgres) nên RLS không chặn app.
-- Supabase mặc định tự cấp quyền cho bảng mới trong public; các lệnh ALTER DEFAULT PRIVILEGES bên dưới
-- tắt việc đó cho bảng tạo về sau. Idempotent; chạy lại sau migration tạo bảng mới để bật RLS cho bảng đó.
--
-- Chạy: powershell -ExecutionPolicy Bypass -File tools\supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db\supabase_lockdown.sql

DO $$
DECLARE
    t text;
BEGIN
    FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
    END LOOP;
END $$;

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM anon, authenticated;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM anon, authenticated;

ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public REVOKE ALL ON TABLES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public REVOKE ALL ON SEQUENCES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM anon, authenticated;
