# ThreadsToxic — Lọc cộng tác thời gian thực

Nhiều người (2–4+) cùng lọc một danh sách bình luận Threads. Mỗi bình luận nằm trong một trong hai mục:

- **Đang lọc**: danh sách chung, ai cũng thấy.
- **Đã loại**: bình luận bị loại khỏi danh sách nhưng vẫn còn trong cơ sở dữ liệu, có thể khôi phục.

Mọi thao tác được đồng bộ ngay trên các máy khác qua SignalR. Khi một người bấm xem một bình luận, người khác thấy ổ khóa kèm tên và không thể loại bình luận đó cùng lúc.

## Yêu cầu

- .NET 10 SDK
- PostgreSQL 18 (đang chạy ở `localhost:5432`, user `postgres`)
- Python 3 + `psycopg2` (chỉ để nạp dữ liệu)

## Thiết lập lần đầu

```powershell
# 1. Tạo database và bảng
psql -h localhost -U postgres -c "CREATE DATABASE threads_toxic_db ENCODING 'UTF8';"
psql -h localhost -U postgres -d threads_toxic_db -f db/schema.sql

# 2. Nạp bình luận từ SQLite của crawler (idempotent, chạy lại an toàn)
python tools/import_comments.py --sqlite "<đường dẫn>\crawlDataThreads\crawlDataThreads\data\threads_data.db"
```

## Chạy

```powershell
cd src\ThreadsToxic.Web
dotnet run
```

Mở `http://localhost:5000`. Các máy khác trong cùng mạng LAN mở `http://<IP-máy-chủ>:5000`. Lần đầu có thể cần cho phép cổng 5000 trong Windows Firewall.

Đổi chuỗi kết nối trong `src/ThreadsToxic.Web/appsettings.json` (mục `ConnectionStrings:Default`) hoặc đổi cổng bằng `--urls`.

## Phím tắt

| Phím | Tác dụng |
| --- | --- |
| `J` / `K` | Xem bình luận kế tiếp / trước đó |
| `Space` | Chọn / bỏ chọn bình luận đang xem |
| `X` | Chuyển bình luận (hoặc các bình luận đã chọn) sang tab còn lại |

## Cấu trúc

```
db/schema.sql                       DDL: bảng comments, moderation_audits, chỉ mục GIN/trigram
tools/import_comments.py            ETL SQLite -> PostgreSQL
src/ThreadsToxic.Web/
  Data/                             Entity Comment (JSONB analysis_data), DbContext
  Endpoints/ItemEndpoints.cs        API: lọc, đếm, chuyển (loại / khôi phục) theo lô
  Hubs/CollabHub.cs                 SignalR: Join, Focus/Blur (lease 45 giây), presence
  Services/CollabState.cs           Trạng thái trong bộ nhớ: người online, khóa
  Services/LeaseSweeper.cs          Mở khóa khi hết hạn
  wwwroot/                          HTML + CSS + JS thuần, SignalR client nội bộ
```

## Ghi chú kỹ thuật

- Thao tác chuyển dùng `UPDATE ... WHERE is_excluded = <trạng thái cũ> RETURNING id` trong một transaction, nên khi nhiều người bấm cùng lúc, mỗi bình luận chỉ đổi một lần và được ghi đúng một dòng vào `moderation_audits`.
- Lease lock chỉ mang tính hiển thị và cảnh báo: nó ngăn người khác loại bình luận đang được xem qua giao diện, không ràng buộc ở tầng cơ sở dữ liệu.
- Trạng thái khóa và presence lưu trong bộ nhớ của một tiến trình. Chạy nhiều máy chủ cần chuyển sang Redis backplane.
- Chưa có đăng nhập: người dùng nhập tên khi vào lần đầu, tên được lưu trong trình duyệt. Muốn xác thực thật thì thêm ASP.NET Identity.
- Chưa có phòng lọc riêng (mọi người đang chung một phòng `filter-room`).
