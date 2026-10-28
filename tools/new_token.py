"""Tạo mã truy cập vĩnh viễn cho một người dùng.

Chạy:
    python tools/new_token.py --index 0 --name "Đạt" --role admin
    python tools/new_token.py --index 1 --name "Minh"

In ra:
- MÃ TRUY CẬP: gửi riêng cho người dùng (họ nhập một lần trên trình duyệt). Không lưu ở đâu khác.
- 3 dòng biến môi trường: dán vào Railway (Variables) hoặc file .env khi chạy local.
  Server chỉ lưu SHA-256 của mã, nên lộ cấu hình cũng không lộ mã.

Đổi mã (khi lộ hoặc người đó nghỉ): chạy lại với cùng --index rồi thay dòng TokenSha256; mã cũ hết hiệu lực
ngay khi server khởi động lại với cấu hình mới. Gỡ hẳn: xóa 3 dòng của index đó (và dồn index cho liên tục).
"""
import argparse
import hashlib
import secrets
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--index", type=int, required=True, help="Số thứ tự người dùng: 0, 1, 2... (không trùng)")
    parser.add_argument("--name", required=True, help="Tên hiển thị, 1-40 ký tự, không trùng người khác")
    parser.add_argument("--role", choices=["admin", "member"], default="member")
    args = parser.parse_args()

    name = args.name.strip()
    if not 1 <= len(name) <= 40:
        sys.exit("Tên phải từ 1 đến 40 ký tự.")

    token = "ttx_" + secrets.token_urlsafe(32)
    digest = hashlib.sha256(token.encode("utf-8")).hexdigest()

    sys.stdout.reconfigure(encoding="utf-8")
    print(f"MÃ TRUY CẬP của {name} (gửi riêng cho người đó, chỉ hiện một lần):")
    print(f"  {token}")
    print()
    print("Biến môi trường (Railway > Variables, hoặc .env khi chạy local):")
    print(f"Auth__Users__{args.index}__Name={name}")
    print(f"Auth__Users__{args.index}__Role={args.role}")
    print(f"Auth__Users__{args.index}__TokenSha256={digest}")


if __name__ == "__main__":
    main()
