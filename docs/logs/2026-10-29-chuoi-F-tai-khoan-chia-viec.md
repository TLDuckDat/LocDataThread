# Nhật ký thay đổi 28–29/10/2026: chuỗi F0–F3, tài khoản vĩnh viễn, chia việc

Nhánh: `improve/p0` (chưa commit). Mục đích: để rà soát lại các yêu cầu đã thỏa chưa, và biết những gì **chưa** được áp dụng lên môi trường thật.

## 1. Yêu cầu và trạng thái

| # | Yêu cầu | Trạng thái | Kiểm chứng |
|---|---|---|---|
| 1 | Chuỗi F0–F3 luôn hợp lý: cha bị loại thì con không được nằm lại (vd F3 giữ mà F2 bỏ) | Xong | 26 test `ThreadChainTests`, e2e "Chuỗi F", kiểm tra trên bản sao dữ liệu thật |
| 2 | Bao phủ cả vòng đời một bình luận: nạp, lọc, khôi phục, hoàn tác, Kho phụ, xuất CSV | Xong | Bảng tình huống ở mục 3 |
| 3 | Token vĩnh viễn cho từng người, thêm 1 admin | Xong | 23 test `AccessTokenTests` + e2e "Đăng nhập" |
| 4 | 4 người làm cùng lúc / khác giờ: chia việc đơn giản, tiện | Xong | 10 test `AssignmentTests` + e2e "Chia việc" |
| 5 | Test toàn bộ dự án: endpoint + frontend, sửa tới khi ổn | Xong | Mục 5 |
| 6 | Áp dụng migration lên Supabase và deploy Railway | **Chưa làm**, cần bạn xác nhận | Mục 6 |

## 2. Hiện trạng dữ liệu thật (đọc từ Supabase, 28/10)

- 40 750 bình luận: 37 091 F0, 2 340 F1, 514 F2, 805 F3.
- `parent_comment_id` thiếu ở **3 528/3 659 bản trả lời (96%)**, nên không dùng được để biết cha.
- Nối cha bằng chuỗi F (cùng bài, cấp L-1, nội dung cha = f(L-1), cùng các cấp trên): **3 659/3 659 khớp đúng một cha**, không nhập nhằng.
- **Đã có 1 vi phạm thật**: bình luận gốc `cmt_C_a8jlWz_U_` bị "noname" loại ngày 18/10, nhưng 3 bình luận bên dưới (F1 `cmt_C_bqXhiy28p` → F2 → F3) vẫn ở "Đang lọc". Migration 007 tự sửa: loại theo, người thao tác `system`, có audit.
- 319 bản trả lời có ngữ cảnh cha dài hơn 300 ký tự. Đây không phải lỗi, nhưng các bình luận cha đó phải được giao cho người xem.

## 3. Tình huống trong vòng đời và cách xử lý

| Tình huống | Trước | Sau |
|---|---|---|
| Loại F2 khi F3 đang lọc | F3 vẫn xuất, ô f2 chứa nội dung đã loại | F3 bị loại theo (`exclude_cascade`, `cause_id`) |
| Loại F0 khi F3 đã được người khác giữ | F3 vẫn xuất | F3 bị loại theo; `reviewed_by` của F3 giữ nguyên tên người đã giữ |
| Một trả lời trong nhánh đang được người khác xem | Loại nửa vời | Cả thao tác bị bỏ qua (`locked`); admin có thể "Vẫn chuyển" |
| Khôi phục F3 khi F2 còn bị loại | Được | Bị chặn (`blocked`); nút "Khôi phục cả chuỗi" |
| Hoàn tác một lần loại | Chỉ bình luận được chọn | Kèm đúng các con bị loại theo lần đó; con bị loại riêng từ trước giữ nguyên |
| Hoàn tác khi cha đã bị người khác loại sau đó | — | Không khôi phục (báo `conflicts`) |
| Nạp: con mới có cha đã loại | Vào "Đang lọc" | Vào "Đã loại" (`system`) |
| Nạp: con mới không tìm được cha | Vẫn nạp | Không nạp, liệt kê id |
| Kho phụ: đưa vào trả lời mà cha chưa có / đã loại | Không kiểm tra | Không đưa (`blockedChain`), vẫn chờ duyệt |
| Kho phụ: đưa cha và con cùng lượt | — | Đưa theo từng cấp, con gắn đúng cha |
| Sửa tay bằng SQL làm sai ràng buộc | Được | DB từ chối lúc commit |
| Xóa bình luận cha còn trả lời | Được nếu không có audit | Khóa ngoại chặn |
| Xuất: ô f cấp trên rỗng sau khi làm sạch | Có dòng f3 mà f1 trống | Bỏ dòng, báo số dòng bị bỏ |
| Xuất "chỉ đã xử lý" | Có thể xuất F1 khi F0 chưa ai xem | Cần cả chuỗi cha đã được xử lý |
| Cha dài hơn 300 ký tự / bị khử trùng | Con vẫn xuất | Giữ nguyên (ngữ cảnh vẫn là văn bản thật) |

## 4. Thay đổi theo file

### Database
- `db/migrations/007_thread_links.sql` (mới):
  - Thêm cột `thread_parent_id`, `thread_root_id`, `moderation_audits.cause_id`, hàm `find_thread_parent`.
  - Điền liên kết cho dữ liệu đã có, tự sửa vi phạm sẵn có.
  - Khóa ngoại và CHECK.
  - Trigger `comments_link_thread`: tự gắn cha, con của cha đã loại thì tự loại.
  - Trigger ghi audit cho bình luận bị loại theo.
  - Constraint trigger hoãn tới lúc commit `comments_exclusion_chain`.
  - Idempotent. Dừng nếu còn bản trả lời không xác định được cha.
- `db/migrations/008_assignment.sql` (mới): cột `assigned_to` và index.
- `db/schema.sql`: thêm các cột mới (ràng buộc và trigger vẫn nằm trong migration).
- `db/checks/chain_integrity.sql` (mới): 7 mục kiểm tra, mọi dòng phải bằng 0.

### Backend
- `Data/ThreadChain.cs` (mới): duyệt con cháu và tổ tiên.
- `Endpoints/ItemEndpoints.cs`:
  - `move` / `undo` viết lại: loại lan xuống con cháu, khôi phục bị chặn hoặc khôi phục cả chuỗi, hoàn tác kèm phần đã kéo theo, trả `409` khi DB từ chối.
  - "Vẫn chuyển" chỉ dành cho admin.
  - Bộ lọc `assigned` và `order=thread`.
  - Trả thêm `threadParentId`, `assignedTo`.
- `Endpoints/ReviewEndpoints.cs`:
  - Đưa vào CSDL chính theo từng cấp, chặn trả lời mồ côi (`blockedChain`).
  - Thao tác hàng loạt chỉ dành cho admin.
- `Endpoints/ExportEndpoints.cs`, `Services/Export/CuratedExport.cs`:
  - Bỏ dòng có chuỗi ngữ cảnh đứt, báo qua `brokenChainDropped` / header `X-Broken-Chain-Dropped`.
  - Chế độ "chỉ đã xử lý" xét cả chuỗi cha.
- `Endpoints/AssignEndpoints.cs` (mới): xem tổng quan, chia đều (theo bài viết, bài lớn trước, cho người ít việc nhất), chuyển phần chưa làm.
- `Services/AccessTokens.cs`, `Services/AuthFilters.cs` (mới):
  - Tài khoản lấy từ `Auth:Users`, chỉ lưu SHA-256 của mã.
  - Mọi `/api` đều cần xác thực.
  - Kiểm tra quyền admin.
- `Hubs/CollabHub.cs`: `Join(mã truy cập)` trả `{ token, name, role }`.
- `Services/CollabState.cs`: lưu cả vai trò của phiên.
- `Program.cs`: không khởi động nếu cấu hình tài khoản thiếu hoặc sai.

### Frontend
- `wwwroot/js/auth.js` (mới): hỏi mã một lần và nhớ; nút "Đổi mã".
- `index.html` / `app.js`:
  - Đăng nhập bằng mã.
  - Bộ lọc "Phần của tôi" và "Theo luồng hội thoại".
  - Tiến độ "Phần của tôi: còn X/Y".
  - Hộp "Chia việc" cho admin.
  - Tự chuyển sang bình luận kế tiếp sau mỗi thao tác.
  - Thông báo loại lan và nút "Khôi phục cả chuỗi".
  - "Vẫn chuyển" chỉ hiện với admin.
- `queue.html` / `queue.js`: đăng nhập bằng mã; ẩn thao tác hàng loạt với member; báo `blockedChain`.
- `export.html` / `export.js`: gửi mã khi gọi API; báo số dòng bị bỏ vì chuỗi đứt.

### Công cụ và cấu hình
- `tools/new_token.py` (mới): tạo mã và 3 dòng `Auth__Users__N__*`.
- `tools/import_comments.py`: nạp theo từng cấp, bỏ qua và liệt kê bản trả lời mồ côi, báo số bình luận tự vào "Đã loại".
- `.env.example`: thêm phần tài khoản.
- `.gitignore`: thêm `.env`. Trước đó file `.env` chứa mật khẩu Supabase **chưa được git bỏ qua**.
- README: các mục Tài khoản, Chia việc, Chuỗi hội thoại, API, Kiểm thử, Cấu trúc.

### Test
- **Sửa hạ tầng test đang hỏng từ trước**: `TestDatabaseContainer.cs` dùng Testcontainers nhưng project chưa tham chiếu gói, nên test không build được. Đã thêm `Testcontainers.PostgreSql` 4.16.0 và nối vào `TestEnvironment`: dùng `THREADS_TEST_CONNECTION` nếu có, nếu không thì tự dựng Docker.
- Nâng `Microsoft.AspNetCore.SignalR.Client` / `Mvc.Testing` lên 10.0.14 (gói 10.0.12 có lỗ hổng GHSA-334f-vvp4-7g6p; chỉ ảnh hưởng project test).
- `TestDb.SeedAsync`: bản trả lời phải khai `Parent`, chuỗi F được dựng từ cha. Thêm `SeedThreadAsync`.
- Test mới: `ThreadChainTests` (26), `AccessTokenTests` (23, gồm kiểm tra cấu hình), `AssignmentTests` (10).
- Test sửa theo thiết kế mới:
  - 3 test chèn trả lời không có cha.
  - 3 test "Vẫn chuyển" của member, chuyển sang admin, và thêm test member bị `403`.
  - `Join_rejects_an_empty_name` đổi thành `Join_rejects_an_unknown_access_token`.
- `tests/ui/e2e.py`: viết lại.
  - Dữ liệu mẫu cũ vi phạm ràng buộc 005 nên script đã hỏng từ trước.
  - Đăng nhập bằng mã.
  - Thêm kiểm tra: đăng nhập, chuỗi F, chia việc, tự nhảy, phân quyền, Kho phụ, trang xuất.

## 5. Kết quả kiểm thử (29/10/2026)

| Bộ test | Kết quả |
|---|---|
| `dotnet test` (xUnit, Postgres 18 local) | **213/213 đạt**. Mốc trước thay đổi: 154/154, sau khi sửa lỗi build |
| `node --test tests/js/util.test.js` | **8/8 đạt** |
| `tests/ui/e2e.py` (Playwright + Edge, app thật) | **68/68 kiểm tra đạt** |
| Kiểm tra đột biến: tắt việc loại lan trong code | 13/26 test chuỗi báo lỗi, chứng tỏ test thật sự bắt lỗi (đã khôi phục code) |
| Migration 007 + 008 trên **bản sao Supabase** (`backups/supabase_20261028_234201.dump`), chạy 2 lần | Ổn, 6 giây; `chain_integrity.sql` = 0 vi phạm |
| `import_comments.py` với SQLite giả lập, chạy 2 lần | Nạp theo cấp, bỏ bản mồ côi, con mới dưới cha đã loại vào "Đã loại" |
| Kiểm tra lỗ hổng gói NuGet | Không còn gói có lỗ hổng |

Hai lỗi phát hiện khi chạy e2e, đều nằm trong **test** chứ không phải trong app:
1. Test bấm nhầm nút "Hoàn tác" của thông báo cũ vẫn còn hiện. Đã sửa bằng cách xóa thông báo cũ và chờ dòng biến mất.
2. Tính năng tự nhảy làm focus của Lan sang c_12 và giữ khóa dòng đó, nên Minh không loại được c_12. Đây là hành vi đúng; test đổi sang c_20.

Ảnh chụp đã xem: hộp Chia việc, màn hình "Phần của tôi", giao diện điện thoại 390px. Đã xác nhận không tràn ngang và tên độc hại hiển thị dạng văn bản.

## 6. Triển khai (29/10/2026)

1. Ghép với `dfff082` (bản đang chạy trên Railway): thư mục làm việc cũ hơn `dfff082` ở một số file (Program.cs thiếu PORT/Supabase, mất bộ đệm trang, xuất theo lô, README phần Supabase/Deploy). Đã ghép lại từng file, giữ phần của `dfff082` và áp thay đổi lần này lên trên; bản trước khi ghép lưu ở nhánh `backup/wip-20261029`. Sau khi ghép: .NET 214/214, JS 13/13, e2e đạt (sửa thêm: Kho phụ hiện nút hàng loạt cho admin ngay khi vào phòng).
2. Sao lưu Supabase: `backups/supabase_20261029_062530.dump`.
3. Migration 007 và 008 đã chạy trên Supabase; `chain_integrity.sql` 0 vi phạm; đúng 3 dòng tự loại theo (`cmt_C_bqXhiy28p`, `cmt_C_bDYClBerN`, `cmt_C_brtOpy4vf`, người thao tác `system`); 3 659 bản trả lời có cha.
4. Tài khoản: admin `hd.dat`; member `tc.tuan`, `nhb.khiem`, `nk.hung`, `nnb.tran`. Mã gốc ở `backups/access_tokens_20261029.txt` (gitignore). Cần đặt 15 dòng `Auth__Users__*` trong Railway > Variables.
5. Code đã push lên `main` (`3bbfb23`), Railway tự deploy.
6. Lưu ý: số liệu cũ ghi tên "Đạt"/"noname"; từ nay ghi theo tên tài khoản (`hd.dat`...).

## 7. Giới hạn đã biết

- Mã truy cập lưu trong localStorage: nên tránh dùng trên máy dùng chung và bấm "Đổi mã" khi rời máy. Đổi mã cần khởi động lại server.
- Khóa, presence và token phiên vẫn nằm trong bộ nhớ của một tiến trình (Railway 1 replica). Khởi động lại thì client tự kết nối lại.
- Chia việc giữ nguyên cả bài viết cho một người. Khi dữ liệu có ít bài viết, phần việc có thể lệch (ví dụ dữ liệu test 42/3). Với 953 bài thật thì phần việc cân đối.
- Loại sẽ kéo theo cả nhánh, nên quy ước là chỉ bấm Loại khi bản thân bình luận hỏng (đã ghi trong README).
