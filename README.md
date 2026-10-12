# ThreadsToxic — Lọc cộng tác thời gian thực

Nhiều người (2–4+) cùng lọc một danh sách bình luận Threads. Mỗi bình luận nằm trong một trong hai mục:

- **Đang lọc**: danh sách chung, ai cũng thấy.
- **Đã loại**: bình luận bị loại khỏi danh sách nhưng vẫn còn trong cơ sở dữ liệu, có thể khôi phục.

Mọi thao tác được đồng bộ ngay trên các máy khác qua SignalR. Khi một người bấm xem một bình luận, người khác thấy ổ khóa kèm tên và server từ chối cho họ loại bình luận đó (trừ khi họ chủ động chọn "Vẫn chuyển").

## Yêu cầu

- .NET 10 SDK
- PostgreSQL 18 (đang chạy ở `localhost:5432`, user `postgres`)
- Python 3 + `psycopg2` (nạp dữ liệu và chạy kiểm thử giao diện)
- Node 20+ (chỉ để chạy test của `util.js`)

## Thiết lập lần đầu

```powershell
# 1. Tạo database và bảng
psql -h localhost -U postgres -c "CREATE DATABASE threads_toxic_db ENCODING 'UTF8';"
psql -h localhost -U postgres -d threads_toxic_db -f db/schema.sql

# 2. Nạp bình luận từ SQLite của crawler (idempotent, chạy lại an toàn)
python tools/import_comments.py --sqlite "<đường dẫn>\crawlDataThreads\crawlDataThreads\data\threads_data.db"
```

### Database đã có từ trước: áp dụng migration

`db/schema.sql` chỉ dùng cho database mới. Database cũ cần chạy các file trong `db/migrations/` theo thứ tự (đều idempotent):

```powershell
psql -h localhost -U postgres -d threads_toxic_db -v ON_ERROR_STOP=1 -f db/migrations/001_audit_restrict.sql
```

| Migration | Tác dụng |
| --- | --- |
| `001_audit_restrict.sql` | `moderation_audits.comment_id` đổi từ `ON DELETE CASCADE` sang `RESTRICT`: không xóa được bình luận đang có nhật ký, nhật ký không biến mất âm thầm |

Nên `pg_dump -Fc` trước khi áp dụng.

## Chạy

```powershell
cd src\ThreadsToxic.Web
dotnet run
```

Mở `http://localhost:5000`. Các máy khác trong cùng mạng LAN mở `http://<IP-máy-chủ>:5000`. Lần đầu có thể cần cho phép cổng 5000 trong Windows Firewall.

Đổi chuỗi kết nối trong `src/ThreadsToxic.Web/appsettings.json` (mục `ConnectionStrings:Default`) hoặc đổi cổng bằng `--urls`.

### Cấu hình khóa

```json
"Collab": { "LeaseSeconds": 45, "SweepSeconds": 5 }
```

`LeaseSeconds` là thời hạn một khóa; frontend gia hạn mỗi 20 giây nên không nên đặt dưới khoảng 30. `SweepSeconds` là chu kỳ quét khóa hết hạn.

## Phím tắt

| Phím | Tác dụng |
| --- | --- |
| `J` / `K` | Xem bình luận kế tiếp / trước đó |
| `Space` | Chọn / bỏ chọn bình luận đang xem |
| `X` | Chuyển bình luận (hoặc các bình luận đã chọn) sang tab còn lại |

Sau mỗi lần chuyển, thông báo có nút **Hoàn tác** trong 8 giây. Nếu có bình luận đang bị đồng đội xem, thông báo có thêm nút **Vẫn chuyển**.

## API

Các endpoint ghi cần header `X-Session-Token`, là token do server cấp khi gọi `Join` trên hub `/hubs/collab`. Tên người thao tác lấy từ phiên phía server, không nhận từ body. Token gắn với một kết nối: mất hiệu lực khi kết nối đóng hoặc `Join` lại, và tuyệt đối không được ghi log.

| Endpoint | Mô tả |
| --- | --- |
| `GET /api/items` | Lọc: `bucket`, `minScore`, `replyLevel`, `keyword`, `word`, `page`, `pageSize`. Trả `total`, `items`, `counts` |
| `GET /api/items/counts` | `{ active, excluded }` |
| `POST /api/items/move` | Body `{ ids, toExcluded, force? }`. Trả `{ moved, movedIds, skipped, locked }`. Bình luận đang bị người **khác** giữ khóa còn hạn không bị đổi và nằm trong `locked`; `force: true` vượt khóa và ghi audit `force_exclude` / `force_restore`. Thiếu hoặc sai token: `401` |
| `POST /api/items/undo` | Body `{ ids, toExcluded }` (`toExcluded` là trạng thái muốn đưa về). Chỉ hoàn tác bình luận mà audit gần nhất là của chính người gọi. Trả `{ undone, undoneIds, conflicts, skipped, locked }`; `conflicts` là bình luận đã bị người khác thao tác sau đó, giữ nguyên |

Sự kiện SignalR: `PresenceChanged`, `LockSnapshot`, `LockChanged`, và `ItemsMoved { ids, toExcluded, by, undo, counts }`. Phiên bản client cũ không có `counts` hoặc `movedIds` vẫn hoạt động.

## Kiểm thử

Test chạy trên database riêng `threads_toxic_test`, **không bao giờ** chạm `threads_toxic_db`. Fixture dừng ngay nếu chuỗi kết nối không trỏ đúng tên database test.

```powershell
# Tạo database test một lần (từ schema.sql, sau đó áp dụng migration)
psql -h localhost -U postgres -c "CREATE DATABASE threads_toxic_test ENCODING 'UTF8';"
psql -h localhost -U postgres -d threads_toxic_test -f db/schema.sql
psql -h localhost -U postgres -d threads_toxic_test -v ON_ERROR_STOP=1 -f db/migrations/001_audit_restrict.sql

# Test tích hợp: API + SignalR trong bộ nhớ
dotnet test

# Test của util.js (esc, safeUrl chống XSS)
node --test tests/js/util.test.js
```

Test giao diện end-to-end (trình duyệt Edge qua Playwright) cần chạy app trỏ vào database test:

```powershell
pip install playwright psycopg2-binary
$env:ConnectionStrings__Default = "Host=localhost;Port=5432;Database=threads_toxic_test;Username=postgres"
dotnet run --project src/ThreadsToxic.Web --urls http://localhost:5055
# cửa sổ khác:
python tests/ui/e2e.py
```

Các test không chạy song song vì dùng chung một database. Dữ liệu test do chính test sinh ra và dọn sạch trước/sau mỗi test.

## Cấu trúc

```
db/schema.sql                       DDL cho database mới
db/migrations/                      Thay đổi schema cho database đã có (idempotent)
tools/import_comments.py            ETL SQLite -> PostgreSQL
src/ThreadsToxic.Web/
  Data/                             Entity Comment (JSONB analysis_data), DbContext
  Endpoints/ItemEndpoints.cs        API: lọc, đếm, chuyển, hoàn tác
  Hubs/CollabHub.cs                 SignalR: Join (cấp token), Focus/Blur, presence
  Services/CollabState.cs           Trạng thái trong bộ nhớ: người online, khóa, token phiên
  Services/CollabOptions.cs         Cấu hình mục "Collab"
  Services/LeaseSweeper.cs          Mở khóa khi hết hạn
  wwwroot/js/util.js                esc / safeUrl / toInt (thuần, test bằng Node)
  wwwroot/                          HTML + CSS + JS thuần, SignalR client nội bộ
tests/ThreadsToxic.Tests/           xUnit: API, SignalR, bảo mật
tests/js/                           Test của util.js
tests/ui/e2e.py                     Test giao diện bằng Playwright
```

## Ghi chú kỹ thuật

- Thao tác chuyển dùng `UPDATE ... WHERE is_excluded = <trạng thái cũ> RETURNING id` trong một transaction, nên khi nhiều người bấm cùng lúc, mỗi bình luận chỉ đổi một lần và được ghi đúng một dòng vào `moderation_audits`.
- Server tôn trọng khóa. Việc kiểm tra khóa và `UPDATE` không nguyên tử: nếu có người xem đúng khoảnh khắc đó thì dòng vẫn đổi và biến mất khỏi màn hình họ; dữ liệu không mất vì khôi phục được.
- Hoàn tác khóa các dòng liên quan (`FOR UPDATE`) trước khi đọc audit, nên không thể ghi đè thao tác của người khác chen vào giữa.
- Danh tính là **tên tự khai** khi `Join`, không phải đăng nhập thật: ai cũng có thể vào dưới bất kỳ tên nào, và hai người có thể trùng tên. Token chỉ ngăn giả mạo từng request. Muốn xác thực thật cần thêm ASP.NET Identity hoặc mật khẩu phòng.
- Trạng thái khóa, presence và token lưu trong bộ nhớ của một tiến trình; khởi động lại server thì mọi người phải tải lại trang. Chạy nhiều máy chủ cần chuyển sang Redis backplane.
- Chưa có phòng lọc riêng (mọi người đang chung một phòng `filter-room`).
