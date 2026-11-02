"""Chuỗi kết nối PostgreSQL mặc định cho các tool: database local khai trong .env ở gốc repo.

Đọc đúng ConnectionStrings__Default mà app dùng (dạng Npgsql), nên chỉ cần sửa một chỗ.
"""
from pathlib import Path

from psycopg2.extensions import make_dsn

ENV_FILE = Path(__file__).resolve().parent.parent / ".env"
KEY = "ConnectionStrings__Default"

# Tên khóa trong chuỗi Npgsql -> tên tham số của psycopg2
NPGSQL_TO_PSYCOPG = {
    "host": "host",
    "server": "host",
    "port": "port",
    "database": "dbname",
    "username": "user",
    "user id": "user",
    "password": "password",
}


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


def parse_npgsql(connection_string):
    """'Host=...;Port=...;Database=...' -> kwargs cho psycopg2; khóa không dùng (Pool Size...) bị bỏ qua."""
    kwargs = {}
    for part in connection_string.split(";"):
        if "=" not in part:
            continue
        key, value = part.split("=", 1)
        name = NPGSQL_TO_PSYCOPG.get(key.strip().lower())
        if name:
            kwargs[name] = value.strip()
    return kwargs


def default_dsn():
    """DSN từ ConnectionStrings__Default trong .env; dừng với thông báo rõ nếu thiếu."""
    if not ENV_FILE.exists():
        raise SystemExit(f"Không thấy {ENV_FILE}. Chép .env.example thành .env rồi điền, hoặc truyền --dsn.")
    connection_string = read_env().get(KEY)
    if not connection_string:
        raise SystemExit(f"Thiếu {KEY} trong {ENV_FILE}, hoặc truyền --dsn.")
    kwargs = parse_npgsql(connection_string)
    if "dbname" not in kwargs or "user" not in kwargs:
        raise SystemExit(f"{KEY} trong {ENV_FILE} thiếu Database hoặc Username.")
    return make_dsn(**kwargs)
