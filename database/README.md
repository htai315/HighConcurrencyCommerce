# Database và fixture phát triển

`001_create_schema.sql` là bản schema 31 bảng được sao chép từ bộ tài liệu nguồn ngày 30/10/2026, giữ nguyên các CHECK/FK/index và chú thích OPEN. File được lưu cùng source để phiên làm việc sau không cần truy cập folder tài liệu ngoài. Đây là baseline thiết kế; chiến lược release/migration được chốt tại [deployment runbook](../docs/operations/deployment.md).

API không tự chạy SQL, migrate hay seed khi khởi động. Với DB đang có schema `store`, tiếp tục dùng connection hiện tại, không chạy lại CREATE TABLE. Với DB local mới và trống, tạo DB riêng, đặt `ConnectionStrings:StoreDb`, rồi áp `001_create_schema.sql`, `002_auth_sessions.sql`, `003_flash_sale_cart_offer.sql` theo đúng thứ tự bằng pgAdmin Query Tool hoặc `psql -v ON_ERROR_STOP=1`. Baseline có transaction và không idempotent; 002 và 003 có thể chạy lại. Compose mount ba script vào init directory nhưng chúng chỉ chạy khi volume PostgreSQL hoàn toàn mới.

## Integration test

- Cần PostgreSQL local (loopback) và role có quyền CREATEDB, kết nối được database quản trị `postgres`.
- Ưu tiên biến môi trường `HCC_TEST_POSTGRES`; nếu không có, dùng credentials từ User Secrets của API `ConnectionStrings:StoreDb`.
- Fixture thay tên database bằng `hcc_tests_<guid>` ngẫu nhiên; không thực thi SQL vào database được ghi trong connection string.
- Mỗi fixture tạo DB, chạy schema nguồn và `HighConcurrencyCommerce.IntegrationTests/CatalogSeed.sql`, chạy HTTP qua WebApplicationFactory rồi DROP đúng DB do fixture tạo. Không enumerate/drop DB khác.
- Chỉ nhận host `localhost`, `127.0.0.1`, `::1`. Seed có account hash cố ý không đăng nhập được, chỉ để đáp ứng FK của product; không dùng làm Admin bootstrap.
- Nếu tiến trình bị kill cứng, cleanup có thể không chạy; kiểm tra DB tạm bằng pgAdmin trước khi dọn thủ công. Không xóa hàng loạt DB theo prefix.

```powershell
# Chỉ cần override nếu không muốn dùng credentials User Secrets API.
$env:HCC_TEST_POSTGRES = "Host=localhost;Port=5432;Database=postgres;Username=<test-role>;Password=<password>"
cd D:\Personal_Project\HighConcurrencyCommerce
dotnet test .\HighConcurrencyCommerce.slnx
```

Catalog tests chỉ đọc. Test seed gồm visible/hidden category, Published/Draft/Archived product, ACTIVE/INACTIVE SKU, nhiều mức giá, tồn available bằng 0 dù physical lớn và SKU chưa có balance. Auth tests ghi account/session vào chính DB tạm, kiểm tra revoke/CSRF/role/rate-limit/race và bootstrap. Chưa có seed dữ liệu commerce vận hành hay concurrency test checkout/payment.

## Upgrade auth 002

`002_auth_sessions.sql` thêm bảng hạ tầng `auth_sessions`, index, seed ba role nếu thiếu và trigger thu hồi phiên khi password/role/status đổi. Transaction bảo vệ việc áp dụng; có thể chạy lại. Baseline vẫn là 31 business entities, auth_sessions không phải business entity E32.

Với DB local đã có baseline 001: `dotnet run --project .\HighConcurrencyCommerce.Api -- --migrate-auth` từ thư mục backend. Lệnh đọc credentials từ config nhưng chỉ cho loopback; không tạo account hay sửa dữ liệu commerce. Workspace đã áp 002 ngày 30/10/2026. Không chạy lại baseline 001 trên DB đã có.

Fixture áp thứ tự baseline → catalog seed → auth upgrade, giữ account mẫu không có password đăng nhập để test tự tạo credentials riêng. Admin thật được bootstrap qua lệnh riêng trong [auth contract](../docs/api/auth.md). Production migration review, Data Protection keys và lịch cleanup session thuộc T30.

## Upgrade Flash Sale cart 003

`003_flash_sale_cart_offer.sql` thêm `cart_items.flash_sale_offer_id`, foreign key và partial index để giữ offer từ banner/product đến checkout. Với database local đã có 001/002, áp riêng migration này trước khi chạy API mới:

```powershell
psql "<connection-string>" -v ON_ERROR_STOP=1 -f .\database\003_flash_sale_cart_offer.sql
```
