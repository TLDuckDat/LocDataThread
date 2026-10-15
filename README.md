# ThreadsToxic — Lọc cộng tác thời gian thực

Nhiều người (2–4+) cùng lọc một danh sách bình luận Threads. Mỗi bình luận nằm trong một trong hai mục:

- **Đang lọc**: danh sách chung, ai cũng thấy.
- **Đã loại**: bình luận bị loại khỏi danh sách nhưng vẫn còn trong cơ sở dữ liệu, có thể khôi phục.

Mọi thao tác được đồng bộ ngay trên các máy khác qua SignalR. Khi một người bấm xem một bình luận, người khác thấy ổ khóa kèm tên và server từ chối cho họ loại bình luận đó (trừ khi họ chủ động chọn "Vẫn chuyển").

Lọc xong, trang **📥 Xuất Excel & CSV** (`/export.html`) xuất danh sách "Đang lọc" ra file (xem [Xuất Excel & CSV](#xuất-excel--csv)). Bình luận đã có người xử lý có thể lọc riêng để xuất.

## Yêu cầu

- .NET 10 SDK
- Database: PostgreSQL trên [Supabase](https://supabase.com) (region Tokyo). Không cần cài Postgres server trên máy
- Công cụ dòng lệnh `psql` / `pg_dump` 17+ (áp dụng migration, sao lưu)
- Docker Desktop (chỉ để chạy test: test tự dựng Postgres tạm trong container)
- Python 3 + `psycopg2` (nạp dữ liệu và chạy kiểm thử giao diện)
- Node 20+ (chỉ để chạy test của `util.js`)

### File `.env`

Thông tin kết nối và khóa Supabase nằm trong `.env` ở gốc repo (đã gitignore, **không commit**). Chép `.env.example` thành `.env` rồi điền:

- `SUPABASE_DB_HOST`, `SUPABASE_DB_USER`, `SUPABASE_DB_PASSWORD`: trên dashboard, **Connect > Direct > Session pooler** (IPv4). Mật khẩu đặt lại ở **Project Settings > Database**.
- `ConnectionStrings__Default`: chuỗi kết nối Npgsql cho app, dạng `Host=...;Port=5432;Database=postgres;Username=postgres.<ref>;Password=...;SSL Mode=Require;Maximum Pool Size=15`.

App (khi chạy ở môi trường Development), các script Python trong `tools/` và các script `tools/supabase_*.ps1` đều đọc từ file này.

## Thiết lập lần đầu

Database Supabase hiện tại đã có đủ bảng và dữ liệu. Các bước dưới chỉ dùng khi dựng một project Supabase mới (đã điền `.env`):

```powershell
# 1. Tạo bảng, áp dụng migration, chặn Data API
powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/schema.sql
Get-ChildItem db/migrations/*.sql | Sort-Object Name | ForEach-Object {
    powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f $_.FullName }
powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/supabase_lockdown.sql

# 2. Nạp bình luận từ SQLite của crawler (idempotent, chạy lại an toàn)
python tools/import_comments.py --sqlite "<đường dẫn>\crawlDataThreads\crawlDataThreads\data\threads_data.db"

# 3. (Tùy chọn) Nạp thêm bình luận từ hàng chờ lọc trùng để lọc lại sau
python tools/import_review.py --crawler "<đường dẫn>\crawlDataThreads\crawlDataThreads"
```

### Nạp từ hàng chờ lọc trùng (`threads_review.db`) vào Kho phụ

Crawler cách ly các bản ghi nghi trùng, không phải tiếng Việt hoặc của Meta AI vào `data/threads_review.db`. `tools/import_review.py` nạp chúng vào **Kho phụ** (bảng `review_queue`), chưa vào "Đang lọc". Người dùng quyết định trên trang [Kho phụ](#kho-phụ) (`/queue.html`).

- Chỉ **đọc** `threads_review.db`, không đổi trạng thái bản ghi bên crawler.
- Bản ghi đã có cùng id trong dữ liệu chính được đánh dấu `exists_in_main` và **tự ẩn** khỏi Kho phụ (thay cho việc đóng thủ công 30 nghìn bản `duplicate_id`).
- Điểm độc hại được tính lại bằng `src/detector/toxic_engine.py` của crawler, cùng thang với dữ liệu chính.
- Chạy lại an toàn: quyết định đã có (đưa vào / loại) và người quyết định không bị ghi đè; chỉ cập nhật số lần gặp và danh sách id bên crawler (`crawler_ids`, dùng cho ghi ngược).
- `--reason non_vietnamese` (lặp lại được) để chỉ nạp một lý do; `--dry-run` để xem trước số lượng.

Dữ liệu cũ (nạp thẳng vào "Đang lọc" theo bản trước) không còn được dùng; nếu có bình luận nào còn `source` khác NULL, kiểm tra trước khi gỡ.

### Database đã có từ trước: áp dụng migration

`db/schema.sql` chỉ dùng cho database mới. Thay đổi schema về sau là một file mới trong `db/migrations/` (idempotent), chạy trên Supabase theo thứ tự:

```powershell
powershell -ExecutionPolicy Bypass -File tools/supabase_backup.ps1   # sao lưu trước
powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/migrations/00X_ten.sql
# Migration tạo bảng mới: chạy lại để bật RLS cho bảng đó
powershell -ExecutionPolicy Bypass -File tools/supabase_psql.ps1 -v ON_ERROR_STOP=1 -f db/supabase_lockdown.sql

# Sau 003: chạy lại import để điền bảng posts và các cột phục vụ xuất file
python tools/import_comments.py --sqlite "<đường dẫn>\crawlDataThreads\crawlDataThreads\data\threads_data.db"
```

| Migration | Tác dụng |
| --- | --- |
| `001_audit_restrict.sql` | `moderation_audits.comment_id` đổi từ `ON DELETE CASCADE` sang `RESTRICT`: không xóa được bình luận đang có nhật ký, nhật ký không biến mất âm thầm |
| `002_comment_source.sql` | Thêm cột `comments.source` (NULL = dữ liệu chính, khác NULL = nạp từ hàng chờ lọc trùng) |
| `004_review_queue.sql` | Thêm `comments.reviewed_by/reviewed_at` (người xử lý gần nhất) và bảng `review_queue` (Kho phụ). Nên chạy trước `import_review.py` |
| `003_export_fields.sql` | Thêm bảng `posts` và các cột `is_reply`, `review_status(_vi)`, `user_review(_vi)`, `user_keywords`, `source_rowid` cho chức năng xuất file. Chạy lại `import_comments.py` để điền: bình luận đã có chỉ được điền các cột này, trạng thái lọc giữ nguyên |
| `005_chain_integrity.sql` | Ràng buộc chuỗi ngữ cảnh F0..F3 (`comments_chain_ck`, `review_queue_chain_ck`) |
| `006_review_incomplete.sql` | Bảng `review_incomplete` |

`db/supabase_lockdown.sql` (chỉ cho Supabase) bật RLS và thu quyền `anon`/`authenticated` trên mọi bảng, kể cả bảng tạo về sau, để publishable key không đọc/ghi được dữ liệu qua Data API. App kết nối bằng chủ sở hữu bảng nên không bị chặn.

## Chạy

```powershell
cd src\ThreadsToxic.Web
dotnet run
```

Mở `http://localhost:5000`. Các máy khác trong cùng mạng LAN mở `http://<IP-máy-chủ>:5000`. Lần đầu có thể cần cho phép cổng 5000 trong Windows Firewall.

App kết nối tới Supabase bằng `ConnectionStrings__Default` trong `.env` (chỉ đọc `.env` ở môi trường Development). Biến môi trường cùng tên luôn được ưu tiên hơn `.env`. Thiếu chuỗi kết nối thì app dừng ngay khi khởi động. Đổi cổng bằng `--urls`.

### Deploy

Supabase chỉ đóng vai trò database; app (.NET + SignalR) chạy trên [Railway](https://railway.com) bằng `Dockerfile` và `railway.json` ở gốc repo.

1. Railway: **New Project > Deploy from GitHub repo**, chọn repo này (nhánh `main`). Railway tự build theo `Dockerfile`.
2. Tab **Variables** của service: thêm `ConnectionStrings__Default` = giá trị cùng tên trong `.env`. Không cần đặt `PORT`: Railway tự cấp, app tự đọc.
3. Tab **Settings > Networking > Generate Domain** để có địa chỉ công khai. Nên chọn region gần Tokyo (Southeast Asia) cho gần Supabase.

- Khi deploy app không đọc `.env` (chỉ đọc ở môi trường Development); image Docker không chứa `.env` (xem `.dockerignore`).
- Chỉ chạy một instance (`numReplicas: 1` trong `railway.json`) vì khóa và phiên nằm trong bộ nhớ; Railway hỗ trợ WebSocket cho SignalR.
- Thử image trên máy: `docker build -t locdatathread .` rồi `docker run -p 8080:8080 -e "ConnectionStrings__Default=..." locdatathread`.

### Bảo vệ bí mật khi commit

`.env`, `backups/`, file dump và khóa/chứng chỉ đã nằm trong `.gitignore`. Thêm một lớp nữa: hook `.githooks/pre-commit` chặn commit có file `.env`, dữ liệu dump, khóa Supabase (`sb_secret_...`, JWT) hoặc mật khẩu trong chuỗi kết nối. Bật một lần sau khi clone:

```powershell
git config core.hooksPath .githooks
```
- Gói Free của Supabase: 500 MB, tự tạm dừng sau 7 ngày không hoạt động, giới hạn 60 kết nối (vì vậy `Maximum Pool Size=15`).

### Sao lưu

Gói Free không có backup tự động. Sao lưu về `backups/` (gitignore):

```powershell
powershell -ExecutionPolicy Bypass -File tools/supabase_backup.ps1
```

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

## Kho phụ

Trang `/queue.html` (nút **📋 Kho phụ** trên thanh trên cùng) chứa bản ghi từ hàng chờ lọc trùng của crawler, chờ quyết định:

- **Đưa vào CSDL chính** (phím `P`): tạo bình luận trong "Đang lọc", gắn `source` = lý do bị cách ly. Bản ghi đã có trong CSDL chính không đưa được.
- **Loại** (phím `R`): đánh dấu bỏ qua. Người loại có thể **Hoàn tác loại**; người khác không hoàn tác được.
- **Hàng loạt**: áp dụng cho mọi bản ghi khớp bộ lọc đang chọn (không chỉ trang đang xem), sau khi xác nhận. Số lượng phải khớp với số đã hiển thị, nếu dữ liệu đổi thì server từ chối (HTTP 409) để không làm trên tập khác với tập đã xem.
- Đưa vào CSDL chính không hoàn tác bằng nút ở đây; bình luận đã vào thì dùng **Loại** ở trang chính (vẫn hoàn tác được).

Mọi thao tác ghi cần token phiên (xem [API](#api)); người thao tác là tên khai khi vào phòng.

### Ghi ngược về crawler

Quyết định trong Kho phụ chỉ nằm ở PostgreSQL. Để crawler không đưa lại các bản ghi đã xử lý:

```powershell
# Tắt crawler trước (SQLite chỉ cho một tiến trình ghi tại một thời điểm)
python tools/sync_review_back.py --crawler "<đường dẫn>\crawlDataThreads\crawlDataThreads" --dry-run
python tools/sync_review_back.py --crawler "<đường dẫn>\crawlDataThreads\crawlDataThreads"
```

Chỉ đẩy quyết định mới (`synced_at` rỗng), chạy lại an toàn. Bình luận "đã đưa vào CSDL chính" chỉ được đổi trạng thái bên crawler, không ghi vào SQLite của crawler vì CSDL chính là PostgreSQL.

## Xuất Excel & CSV

Bản port trang "📥 Xuất dữ liệu Excel & CSV" của crawler (bảng **4 cột chuẩn để tự đánh giá**), bỏ phần quét dữ liệu. Nội dung file giống crawler 100%:

- Cùng 11 cột (`Mã ID`, `Bài viết`, `f0`–`f3`, `Nội dung`, `Điểm đánh giá`, `Chủ đề bài viết`, `Tự đánh giá…`, `Từ lóng mới…`), cùng bộ lọc (nhóm Xấu luôn / Chưa rõ / Trong sạch, gốc / con, độ dài 2–300 ký tự, khử trùng lặp), chỉ giữ tiếng Việt và loại Meta AI như crawler.
- Excel: sheet `Dữ liệu đánh giá`, độ rộng cột cố định, wrap text, font Segoe UI, header nền `#0F172A`, cột tự đánh giá nền vàng, chiều cao hàng tự tính theo độ dài. CSV: UTF-8 BOM, mở bằng Excel không lỗi font.
- Tên file như crawler: `threads_danh_gia_4_cot_<nhóm>_<YYYYMMDD_HHMMSS>.xlsx|csv`.
- Chọn / ẩn cột (kèm 4 bộ cột có sẵn), thứ tự cột trong file đúng thứ tự đã chọn. Xem trước lấy 60 bình luận mới nhất rồi mới lọc, giống crawler.

Khác crawler ở hai điểm có chủ đích:

- Chỉ xuất bình luận trong mục **Đang lọc**. Bình luận đã chuyển sang **Đã loại** không có trong file (tương đương đã xóa bên crawler).
- Ô bắt đầu bằng `=` (vd `=)))`) được ghi là văn bản. Bên crawler, openpyxl ghi chúng thành công thức hỏng khiến Excel báo lỗi khi mở; giá trị hiển thị vẫn giống nhau.

Bảng "chi tiết đầy đủ thuộc tính" và JSON của crawler không được chuyển sang: bên crawler chức năng này đang lỗi (`KeyError: 'post_author_username'`).

Bộ lọc tiếng Việt là bản port C# của `src/detector/text_normalizer.py` (`Services/Export/VietText.cs`). Phần Unicode của Python mà .NET không có (`lower`/`casefold`, chữ cái mang tên "LATIN", tập ký tự của `re.IGNORECASE`) nằm trong `PyUnicode.g.cs`, sinh bằng `python tools/gen_py_unicode.py` (dùng đúng phiên bản Python của crawler).

## API

Các endpoint ghi cần header `X-Session-Token`, là token do server cấp khi gọi `Join` trên hub `/hubs/collab`. Tên người thao tác lấy từ phiên phía server, không nhận từ body. Token gắn với một kết nối: mất hiệu lực khi kết nối đóng hoặc `Join` lại, và tuyệt đối không được ghi log.

| Endpoint | Mô tả |
| --- | --- |
| `GET /api/items` | Lọc: `bucket`, `minScore`, `replyLevel`, `keyword`, `word`, `source` (`main` \| `review` \| lý do cụ thể), `page`, `pageSize`. Trả `total`, `items`, `counts` |
| `GET /api/items/counts` | `{ active, excluded }` |
| `POST /api/items/move` | Body `{ ids, toExcluded, force? }`. Trả `{ moved, movedIds, skipped, locked }`. Bình luận đang bị người **khác** giữ khóa còn hạn không bị đổi và nằm trong `locked`; `force: true` vượt khóa và ghi audit `force_exclude` / `force_restore`. Thiếu hoặc sai token: `401` |
| `GET /api/export/curated` | Xem trước (60 dòng): `status` (`all` \| `bad` \| `ambiguous` \| `clean`), `type` (`all` \| `root` \| `reply`), `minLength` (0–500, mặc định 2), `maxLength` (10–10000, mặc định 300), `dedup` (mặc định `true`), `reviewed` (`true`: chỉ bình luận đã có người xử lý). Trả `{ columns, rows, previewLimit }` |
| `GET /api/export/curated/xlsx`, `/csv` | Tải file với cùng tham số, thêm `columns` (lặp lại được, theo thứ tự; trống = tất cả). Trang xuất mặc định chọn 6 cột: Bài viết, f0–f3, Nội dung |
| `GET /api/items/progress` | Đã xử lý / tổng, và theo từng người (tổng và trong ngày) |
| `POST /api/items/approve` | Body `{ ids }`. Giữ lại: đánh dấu đã duyệt, bình luận vẫn ở "Đang lọc". Ghi audit `approve` |
| `GET /api/review` | Kho phụ: `decision` (`pending` \| `promoted` \| `rejected`), `type`, `reason`, `keyword`, `inMain` (`hide` \| `only` \| `all`), `page`, `pageSize`. Trả `{ total, items, counts }` |
| `GET /api/review/counts` | Chỉ số đếm của Kho phụ (`counts` ở trên), không tải bản ghi: dùng cho huy hiệu ở trang chính |
| `POST /api/review/promote`, `/reject`, `/restore` | Body `{ ids }` (tối đa 500). Cần `X-Session-Token`. `restore` chỉ hoàn tác loại do chính người đó thực hiện |
| `POST /api/review/bulk` | Body `{ action: promote \| reject, type, reason, keyword, expectedCount }`. Trả `409` nếu số lượng khớp không bằng `expectedCount`; tối đa 20 000 bản ghi |
| `POST /api/items/undo` | Body `{ ids, toExcluded }` (`toExcluded` là trạng thái muốn đưa về). Chỉ hoàn tác bình luận mà audit gần nhất là của chính người gọi. Trả `{ undone, undoneIds, conflicts, skipped, locked }`; `conflicts` là bình luận đã bị người khác thao tác sau đó, giữ nguyên |

Sự kiện SignalR: `PresenceChanged`, `LockSnapshot`, `LockChanged`, và `ItemsMoved { ids, toExcluded, by, undo, counts }`. Phiên bản client cũ không có `counts` hoặc `movedIds` vẫn hoạt động.

## Kiểm thử

Test **không bao giờ** chạm Supabase. `dotnet test` tự dựng một Postgres 17 tạm trong Docker (Testcontainers) từ `db/schema.sql` và `db/migrations/`, xóa khi chạy xong, nên cần Docker Desktop đang bật. Fixture dừng ngay nếu chuỗi kết nối không trỏ tới database tên `threads_toxic_test`. Muốn dùng database có sẵn thay cho container: đặt `THREADS_TEST_CONNECTION`.

```powershell
# Test tích hợp: API + SignalR trong bộ nhớ
dotnet test

# Test của util.js (esc, safeUrl chống XSS)
node --test tests/js/util.test.js
```

Test giao diện end-to-end (trình duyệt Edge qua Playwright) cần chạy app trỏ vào database test trong Docker:

```powershell
pip install playwright psycopg2-binary
powershell -ExecutionPolicy Bypass -File tools/start_test_db.ps1   # Postgres test ở localhost:55432
$env:ConnectionStrings__Default = "Host=localhost;Port=55432;Database=threads_toxic_test;Username=postgres;Password=postgres"
dotnet run --project src/ThreadsToxic.Web --urls http://localhost:5055
# cửa sổ khác:
python tests/ui/e2e.py
docker stop threads-test-db                                          # xong thì dừng (container tự xóa)
```

Các test không chạy song song vì dùng chung một database. Dữ liệu test do chính test sinh ra và dọn sạch trước/sau mỗi test.

## Cấu trúc

```
db/schema.sql                       DDL cho database mới
db/migrations/                      Thay đổi schema cho database đã có (idempotent)
db/supabase_lockdown.sql            Chặn Data API của Supabase khỏi bảng của app
.env.example                        Mẫu file .env (kết nối và khóa Supabase)
Dockerfile, .dockerignore           Image chạy app (Railway)
railway.json                        Cấu hình deploy Railway (1 replica, healthcheck)
.githooks/pre-commit                Chặn commit lộ bí mật
tools/supabase_psql.ps1             psql tới Supabase bằng thông tin trong .env
tools/supabase_backup.ps1           Sao lưu Supabase về backups/
tools/supabase_env.ps1              Đọc .env cho hai script trên
tools/start_test_db.ps1             Postgres test trong Docker cho e2e
tools/db_env.py                     DSN mặc định (Supabase trong .env) cho script Python
tools/import_comments.py            ETL SQLite -> PostgreSQL
tools/import_review.py              Hàng chờ lọc trùng (threads_review.db) -> Kho phụ (review_queue)
tools/sync_review_back.py           Ghi ngược quyết định Kho phụ về threads_review.db của crawler
tools/gen_py_unicode.py             Sinh bảng Unicode của Python cho bản port bộ lọc tiếng Việt
src/ThreadsToxic.Web/
  Data/                             Entity Comment (JSONB analysis_data), Post, DbContext
  Endpoints/ItemEndpoints.cs        API: lọc, đếm, chuyển, hoàn tác
  Endpoints/ExportEndpoints.cs      API: xem trước và tải file Excel/CSV
  Services/Export/                  Bản port exporter của crawler: lọc tiếng Việt, dựng bảng, ghi xlsx/csv
  Hubs/CollabHub.cs                 SignalR: Join (cấp token), Focus/Blur, presence
  Services/CollabState.cs           Trạng thái trong bộ nhớ: người online, khóa, token phiên
  Services/CollabOptions.cs         Cấu hình mục "Collab"
  Services/LeaseSweeper.cs          Mở khóa khi hết hạn
  Data/ParallelQuery.cs             Truy vấn đọc trên DbContext riêng để chạy song song, thử lại khi đứt kết nối
  wwwroot/js/util.js                esc / safeUrl / toInt / bộ đệm trang tải trước (thuần, test bằng Node)
  wwwroot/                          HTML + CSS + JS thuần, SignalR client nội bộ (export.html: trang xuất file)
tests/ThreadsToxic.Tests/           xUnit: API, SignalR, bảo mật (Postgres tạm trong Docker)
tests/js/                           Test của util.js
tests/ui/e2e.py                     Test giao diện bằng Playwright
```

## Ghi chú kỹ thuật

- Mỗi lượt đi về Supabase mất vài trăm ms (khi app chạy xa Tokyo), nên dữ liệu được tải từ từ và song song thay vì một lần:
  - Các API đọc (`/api/items`, `/api/review`, `/api/items/progress`) chạy các truy vấn độc lập song song, mỗi truy vấn một kết nối (`IDbContextFactory`, xem `Data/ParallelQuery.cs`).
  - Xuất file lấy danh sách id theo thứ tự trước, rồi tải bình luận theo lô 2 000 dòng (4 lô cùng lúc, lô đứt kết nối thì tải lại) và bài viết một lần, sau đó ghép lại đúng thứ tự. Kết quả giống hệt cách tải một lần trước đây.
  - Trang chính và Kho phụ tải trước trang kế tiếp ở nền (bấm "Sau →" hiện ngay), bỏ bản tải trước khi dữ liệu đổi. Trong lúc tải, danh sách cũ vẫn hiện và mờ đi. Dữ liệu tải song song với lúc kết nối SignalR.
  - Trang xuất file hiện thời gian chờ khi server đang tạo file và % khi tải về.
- Thao tác chuyển dùng `UPDATE ... WHERE is_excluded = <trạng thái cũ> RETURNING id` trong một transaction, nên khi nhiều người bấm cùng lúc, mỗi bình luận chỉ đổi một lần và được ghi đúng một dòng vào `moderation_audits`.
- Server tôn trọng khóa. Việc kiểm tra khóa và `UPDATE` không nguyên tử: nếu có người xem đúng khoảnh khắc đó thì dòng vẫn đổi và biến mất khỏi màn hình họ; dữ liệu không mất vì khôi phục được.
- Hoàn tác khóa các dòng liên quan (`FOR UPDATE`) trước khi đọc audit, nên không thể ghi đè thao tác của người khác chen vào giữa.
- Danh tính là **tên tự khai** khi `Join`, không phải đăng nhập thật: ai cũng có thể vào dưới bất kỳ tên nào, và hai người có thể trùng tên. Token chỉ ngăn giả mạo từng request. Muốn xác thực thật cần thêm ASP.NET Identity hoặc mật khẩu phòng.
- Trạng thái khóa, presence và token lưu trong bộ nhớ của một tiến trình; khởi động lại server thì mọi người phải tải lại trang. Chạy nhiều máy chủ cần chuyển sang Redis backplane.
- Chưa có phòng lọc riêng (mọi người đang chung một phòng `filter-room`).
