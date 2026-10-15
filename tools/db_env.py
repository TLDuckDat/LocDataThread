"""Chuỗi kết nối PostgreSQL mặc định cho các tool: database Supabase khai trong .env ở gốc repo."""
from pathlib import Path

from psycopg2.extensions import make_dsn

ENV_FILE = Path(__file__).resolve().parent.parent / ".env"


def read_env(path=ENV_FILE):
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        values[key.strip()] = value
    return values


def default_dsn():
    """DSN từ SUPABASE_DB_* trong .env; dừng với thông báo rõ nếu thiếu."""
    if not ENV_FILE.exists():
        raise SystemExit(f"Không thấy {ENV_FILE}. Chép .env.example thành .env rồi điền, hoặc truyền --dsn.")
    env = read_env()
    missing = [k for k in ("SUPABASE_DB_HOST", "SUPABASE_DB_USER", "SUPABASE_DB_PASSWORD") if not env.get(k)]
    if missing:
        raise SystemExit(f"Thiếu {', '.join(missing)} trong {ENV_FILE}, hoặc truyền --dsn.")
    return make_dsn(
        host=env["SUPABASE_DB_HOST"],
        port=env.get("SUPABASE_DB_PORT") or "5432",
        dbname=env.get("SUPABASE_DB_NAME") or "postgres",
        user=env["SUPABASE_DB_USER"],
        password=env["SUPABASE_DB_PASSWORD"],
        sslmode=env.get("SUPABASE_DB_SSLMODE") or "require",
    )
