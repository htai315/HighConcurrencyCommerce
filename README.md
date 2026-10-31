# High-Concurrency E-Commerce Platform

> Tài liệu tổng quan duy nhất dành cho việc phát triển dự án. Nội dung này hợp nhất Business Scope, SRS, Permission Matrix, Use Cases, State Models, Architecture, Sequence, ERD, Physical Database, API Specification và trạng thái source code tại ngày **30/10/2026**.

## 1. Mục đích của README

README này trả lời bốn câu hỏi:

1. Sản phẩm cần giải quyết bài toán gì?
2. Những quy tắc nghiệp vụ và quy tắc đồng thời nào bắt buộc phải giữ đúng?
3. Kiến trúc, dữ liệu và API dự kiến được tổ chức ra sao?
4. Source code hiện tại đã làm đến đâu và cần tiếp tục theo thứ tự nào?

Đây là tài liệu onboarding và định hướng triển khai. Khi nội dung trong README mâu thuẫn với code đang chạy, cần xác định nguyên nhân và cập nhật cả hai; không âm thầm thay đổi business rule. Những mục gắn nhãn **OPEN**, **PROPOSED**, **DX** hoặc **VERIFY** chưa phải quyết định cuối.

---

## 2. Tóm tắt sản phẩm

**Ngành hàng đã được chủ dự án chốt:** shop thể thao chuyên cầu lông — vợt, áo/trang phục, hộp cầu và phụ kiện. Tên hiện tại HighCommerce; hướng hình ảnh cobalt sân đấu, nền sáng và minh họa vợt/cầu. Xem [concept giao diện](docs/design/badminton-auth.md). Không dùng lại định vị cửa hàng công nghệ.

Đây là website thương mại điện tử B2C cho **một cửa hàng và một kho chính**, không phải marketplace. Khách hàng có thể xem sản phẩm, quản lý giỏ hàng, checkout, thanh toán PayOS hoặc COD và theo dõi đơn. Staff vận hành sản phẩm, kho, đơn và giao hàng mô phỏng. Admin quản lý tài khoản nội bộ, giá, công bố sản phẩm, Flash Sale, phê duyệt nghiệp vụ nhạy cảm, đối soát và audit.

Điểm khác biệt quan trọng nhất của dự án là tính đúng đắn trong điều kiện đồng thời cao:

- Không bán vượt tồn kho.
- Không giữ một phần đơn nhiều SKU nếu một SKU thất bại.
- Không vượt quota Flash Sale hoặc giới hạn mua theo khách hàng.
- Retry checkout không tạo đơn trùng.
- Callback PayOS lặp không tạo tác dụng hoặc giao dịch trùng.
- Callback, Worker hết hạn và thao tác hủy cạnh tranh nhưng chỉ có một kết quả cuối.
- Kho không bị nhả hoặc hoàn hai lần.
- Mọi khoản tiền thực thu, kể cả tiền đến muộn hoặc thu dư, đều được ghi nhận để đối soát.

### Phạm vi nền tảng

| Thuộc tính | Quyết định |
|---|---|
| Mô hình | Single-store B2C |
| Kho | Một kho chính |
| Người dùng | Guest, Customer, Staff, Admin |
| Frontend mục tiêu | React |
| Backend | ASP.NET Core / C# |
| Database | PostgreSQL, nguồn sự thật cho nghiệp vụ |
| Thanh toán | PayOS và COD |
| Giao hàng | Mô phỏng trạng thái, không tích hợp hãng vận chuyển thật |
| Thông báo | In-app trong MVP |
| Kiến trúc backend mục tiêu | Modular Monolith |

### Ngoài phạm vi hiện tại

- Marketplace nhiều người bán, chia hoa hồng hoặc chia tiền.
- Nhiều kho và luân chuyển hàng giữa kho.
- Tích hợp hãng vận chuyển thật, tracking và SLA vận chuyển.
- Tự động refund qua API PayOS.
- Livestream, đấu giá, trả góp, loyalty và cá nhân hóa.
- Review sản phẩm, voucher phức tạp và đổi/trả sau giao hàng.
- Email/SMS.
- BI nâng cao.
- Cam kết uptime, p95/p99, RPS và benchmark load/stress/soak hoàn chỉnh.

---

## 3. Trạng thái source code hiện tại

Frontend và backend được tách thành hai workspace độc lập ở cùng project root:

```text
D:\Personal_Project\
├── frontend/                         # React + TypeScript + Vite
├── HighConcurrencyCommerce/          # ASP.NET Core/C# backend
│   ├── HighConcurrencyCommerce.Api/
│   ├── HighConcurrencyCommerce.Application/
│   ├── HighConcurrencyCommerce.Domain/
│   ├── HighConcurrencyCommerce.Infrastructure/
│   ├── HighConcurrencyCommerce.IntegrationTests/
│   └── HighConcurrencyCommerce.slnx
├── database/                         # Schema nguồn, không tự chạy vào DB ứng dụng
├── ops/                              # Script backup/restore PostgreSQL có safety guard
├── compose.yaml                      # Smoke stack React/Nginx + API + PostgreSQL
├── docs/TASKS.md                     # 30 task và trạng thái triển khai
├── docs/api/catalog.md               # Contract catalog đã implement
└── README.md
```

### Đã có

- Solution và project references theo hướng phân lớp.
- ASP.NET Core Web API chạy trên `net10.0`.
- OpenAPI được bật ở môi trường Development.
- Npgsql và Entity Framework Core 10.
- `StoreDbContext` ánh xạ schema PostgreSQL `store`.
- Đủ 31 model E01–E31 cùng navigation properties, index và quan hệ được scaffold từ database.
- Connection string `ConnectionStrings:StoreDb` đang được cấu hình bằng .NET User Secrets, không nằm trong `appsettings.json`.
- Build đã được kiểm tra bằng .NET SDK `10.0.403`: thành công, 0 warning, 0 error tại ngày 30/10/2026.
- Workspace frontend React 19 + TypeScript + Vite tách riêng tại `frontend/`.
- React Router cho Storefront, Cart, Orders, Staff, Admin, Login và trang 404.
- HTTP client dùng `/api/v1`; Vite proxy sang backend local `http://localhost:5291`.
- Public catalog đã chạy xuyên suốt: PostgreSQL → `ICatalogReader` → 3 API GET → React Router loaders.
- Tìm tên, lọc danh mục/khoảng giá, sắp xếp, phân trang; trang chi tiết chọn ACTIVE SKU và xem tồn khả dụng.
- DTO công khai chỉ có dữ liệu cần hiển thị; không trả EF entity, tài khoản hoặc số lượng reserved/allocated.
- React validate response lúc chạy; có loading/empty/error/retry, bộ lọc trong URL và request cancellation. TypeScript bật strict.
- Schema nguồn 31 bảng ở `database/001_create_schema.sql`; fixture test tạo/xóa database PostgreSQL riêng.
- Auth đã triển khai theo [ADR 0001](docs/adr/0001-auth-sessions.md): session PostgreSQL, cookie HttpOnly, CSRF, register/login/logout/me/password và Admin lock/unlock account có audit.
- React có đăng ký/đăng nhập/đăng xuất/đổi mật khẩu thật, header theo session và route guard Customer/Staff/Admin.
- SQL upgrade auth `database/002_auth_sessions.sql`, lệnh migrate local và bootstrap Admin đầu tiên (không có default credential).
- Hồ sơ và sổ địa chỉ Customer (T11): GET/PATCH profile, GET/POST/PATCH/DELETE addresses có phân quyền Customer, owner isolation tuyệt đối, transaction khóa row UserAccount để đảm bảo chỉ có tối đa 1 địa chỉ mặc định dưới concurrency, và chặn xóa địa chỉ đã gắn với đơn hàng.
- React có trang `/account` cho khách hàng quản lý hồ sơ và sổ địa chỉ, đồng bộ styling sân cầu lông.
- Product/Media T12, Inventory T13–T14, Cart T15, Checkout T17, COD T18, Fulfillment T25–T26, Notification T27, Reporting T28, deployment hardening T30 và Customer Order Center T31 đã triển khai. T30 bổ sung liveness/readiness, correlation + JSON logs, persistent Data Protection keys, Docker/Compose và backup/restore runbook. T16 là ADR nền cho checkout/COD; ADR 0003 hoãn PayOS để hoàn thiện COD trước.
- Kiểm chứng hiện tại: 116 BE integration tests + 48 FE tests pass; AC-20a 100 buyer/10 tồn đã pass; .NET/React build và lint pass. Browser smoke Edge cho login/register ở desktop/mobile đã pass; chưa có E2E toàn bộ commerce hoặc benchmark load/stress/soak.

### Chưa có

- Password reset qua email, email verification, quản lý Staff/role và production auth deployment.
- Domain aggregate, state transition guard và business invariant trong project Domain.
- EF migrations/versioned upgrade strategy và seed dữ liệu vận hành. Seed hiện có chỉ phục vụ test.
- Transactional Outbox, broker và notification delivery/push worker; notification trong app hiện commit trực tiếp cùng transaction PostgreSQL.
- PayOS client và webhook verification.
- Redis, RabbitMQ hoặc Outbox.
- Payment provider/webhook; concurrency hiện đã bao phủ product publish, inventory, cart merge, checkout giữ tồn, Customer cancel-vs-Staff-confirm, COD confirm/expiry, giao thành công/thu tiền, retry-vs-return và hoàn kho exactly-once.
- CI/CD pipeline theo nhà cung cấp, production ingress manifest và hạ tầng HA/managed PostgreSQL cụ thể.
- Giá Flash Sale trên catalog, checkout/order/payment và dashboard nghiệp vụ các module tiếp theo.

### Ý nghĩa của trạng thái hiện tại

`StoreDbContext` và 31 model chứng minh lớp persistence đã được scaffold, nhưng **không đồng nghĩa nghiệp vụ đã được triển khai**. Nhiều invariant liên bảng không thể chỉ dựa vào model/FK và phải được thực hiện bằng application transaction, guarded SQL và integration test.

`Class1.cs` trong Domain vẫn là placeholder; public catalog chỉ đọc nên chưa tạo domain aggregate. Application và Infrastructure đã có module Catalog.

Tiến độ theo task nằm tại [docs/TASKS.md](docs/TASKS.md). T01–T18, T23–T28 và T30–T31 đã hoàn tất; T29 AC-20a pass, AC-20b/c deferred cùng T19–T22 theo ADR 0003. Contract chạy thật: [catalog](docs/api/catalog.md), [auth](docs/api/auth.md), [account](docs/api/account.md), [cart](docs/api/cart.md), [checkout](docs/api/checkout.md), [Customer orders](docs/api/customer-orders.md), [COD confirmation](docs/api/cod-orders.md), [fulfillment](docs/api/fulfillment.md), [notifications](docs/api/notifications.md), [reporting](docs/api/reporting.md), [flash sale](docs/api/flash-sale.md); vận hành: [deployment](docs/operations/deployment.md), [backup/recovery](docs/operations/backup-recovery.md); quyết định: [ADR 0002](docs/adr/0002-checkout-idempotency-and-cod-void.md), [ADR 0003](docs/adr/0003-defer-payos-complete-cod-first.md).

---

## 4. Cách build và chạy backend hiện tại

### Yêu cầu

- .NET SDK 10.x.
- PostgreSQL có schema `store` tương thích với 31 model hiện tại.
- Connection string được cấp qua User Secrets hoặc biến môi trường.

### Thiết lập connection string cho máy phát triển

Không commit mật khẩu vào Git. Từ thư mục `HighConcurrencyCommerce`:

```powershell
dotnet user-secrets set "ConnectionStrings:StoreDb" "Host=localhost;Port=5432;Database=<database>;Username=<username>;Password=<password>" --project .\HighConcurrencyCommerce.Api\HighConcurrencyCommerce.Api.csproj
```

Hoặc dùng biến môi trường:

```powershell
$env:ConnectionStrings__StoreDb = "Host=localhost;Port=5432;Database=<database>;Username=<username>;Password=<password>"
```

### Build

```powershell
cd D:\Personal_Project\HighConcurrencyCommerce
dotnet restore
dotnet build .\HighConcurrencyCommerce.slnx
```

### Chạy API

```powershell
dotnet run --project .\HighConcurrencyCommerce.Api\HighConcurrencyCommerce.Api.csproj
```

Launch profile hiện tại:

- HTTP: `http://localhost:5291`
- HTTPS: `https://localhost:7217`
- OpenAPI Development: `/openapi/v1.json`

Lưu ý: tài liệu thiết kế cũ từng đề xuất cổng 5080, nhưng source code hiện tại dùng 5291/7217; khi chạy local phải theo `launchSettings.json`.

### Hạn chế khi chạy ở trạng thái hiện tại

API đã cung cấp `GET /api/v1/catalog/categories`, `GET /api/v1/catalog/products` và `GET /api/v1/catalog/products/{id}`. Chạy bằng profile `http` để khớp Vite proxy. Development không redirect HTTP sang HTTPS; môi trường triển khai vẫn cần TLS và cấu hình reverse proxy phù hợp.

Nếu database trống, catalog trả danh sách rỗng; không tự tạo dữ liệu mẫu hay ghi dữ liệu vào DB ứng dụng. Với DB mới, xem [database/README.md](database/README.md) trước khi áp schema. Cách chạy React ở [frontend/README.md](frontend/README.md).

### Kiểm thử hiện tại

```powershell
cd D:\Personal_Project\HighConcurrencyCommerce
dotnet test .\HighConcurrencyCommerce.slnx
cd ..\frontend
npm test
npm run build
npm run lint
```

BE test dùng PostgreSQL loopback với role có quyền CREATEDB, lấy credentials từ `HCC_TEST_POSTGRES` hoặc User Secrets API. Nó tạo `hcc_tests_<guid>`, áp schema/seed và tự dọn đúng DB đó; không dùng database ứng dụng để chạy schema/seed. FE test dùng Vitest + Testing Library + MSW (mock tại HTTP boundary), không thay thế browser E2E hoặc load test.

### Xác thực đã chốt và cách chạy

Chủ dự án đã đồng ý **cookie HttpOnly + session PostgreSQL** (ADR 0001). Cookie chỉ chứa token ngẫu nhiên 256 bit, DB chỉ lưu SHA-256; hết hạn tuyệt đối sau 7 ngày, không tự gia hạn. Production: Secure, SameSite=Lax, Path=/, tên `__Host-hcc-session`; HTTP Development dùng `hcc-session`. FE/API cùng origin qua proxy, không lưu token trong localStorage.

GET `/api/v1/auth/csrf` lấy request token; mọi mutation hiện tại gửi `X-CSRF-TOKEN`. Register chỉ tạo CUSTOMER, email trim/lowercase và unique index chống race. Password dùng Identity PasswordHasher PBKDF2 210000 iterations; password mới 12–128 ký tự, có chữ hoa A–Z, chữ thường a–z, số 0–9 và ký tự đặc biệt (ví dụ @). Register bắt buộc nhập lại qua `confirmPassword`, frontend và backend cùng kiểm tra. Đăng ký mới/trùng email đều 202; đăng nhập email không có/password sai/account locked đều lỗi chung 401. Không áp complexity mới để chặn login tài khoản cũ.

Login xoay session, logout hủy phiên hiện tại; đổi password/role/status hủy mọi phiên bằng trigger PostgreSQL trong transaction. Auth kiểm tra DB mỗi request; role lấy từ DB. Admin lock/unlock có audit, không cho tự đổi trạng thái chính mình. Chính sách Customer/Staff/Admin riêng, không mặc định kế thừa quyền; các DX ngoài me/logout vẫn chưa mở. Rate limit mặc định login 10/phút, register 5/giờ, password 5/phút theo remote IP (in-memory; chưa distributed).

Với DB đã có baseline 001, nâng cấp auth rồi chạy API từ thư mục `HighConcurrencyCommerce`:

```powershell
dotnet run --project .\HighConcurrencyCommerce.Api -- --migrate-auth
dotnet run --project .\HighConcurrencyCommerce.Api --launch-profile http
```

Lệnh migrate chỉ chấp nhận PostgreSQL loopback, áp upgrade 002 có transaction, không tạo tài khoản. Workspace local đã áp ngày 30/10/2026. API khởi động bình thường không tự migrate. Với DB mới: áp baseline 001 trước rồi upgrade 002, không chạy lại 001 trên DB đã có.

Tạo Admin đầu tiên bằng lệnh riêng:

```powershell
dotnet run --project .\HighConcurrencyCommerce.Api -- --bootstrap-admin
```

Nhập email/họ tên/password; password được ẩn và yêu cầu xác nhận. Chỉ tạo nếu chưa có Admin, kể cả khi chạy đồng thời; không nâng quyền email hiện hữu, không có default password. Bootstrap đã được test trên DB tạm; chưa tạo Admin giả trong database của người dùng. Endpoint chạy thật và cấu hình production còn cần hoàn thiện được ghi tại [auth contract](docs/api/auth.md).

---

## 5. Vai trò và phân quyền

### Guest

- Là phiên chưa đăng nhập, không phải role trong database.
- Xem danh mục, sản phẩm và Flash Sale công khai.
- Đăng ký Customer và đăng nhập.
- Giỏ Guest lưu ở client.
- Không được checkout.

### Customer

- Quản lý hồ sơ và địa chỉ của chính mình.
- Quản lý giỏ server và gộp giỏ Guest sau khi đăng nhập.
- Checkout PayOS hoặc COD theo điều kiện.
- Xem đơn, thanh toán và thông báo của chính mình.
- Gửi yêu cầu hủy khi trạng thái cho phép.
- Tài khoản `LOCKED` không được đăng nhập hoặc tạo đơn mới; đơn đã tạo vẫn tiếp tục được Staff xử lý.

### Staff

- Tài khoản do Admin tạo, không đăng ký công khai.
- Tạo/sửa sản phẩm ở trạng thái Draft.
- Nhập tăng kho.
- Gửi yêu cầu giảm tồn hoặc xử lý chênh lệch kiểm kê.
- Xác nhận COD, tiếp nhận đơn, đóng gói và mô phỏng giao hàng.
- Gửi yêu cầu hủy đơn PayOS đã thanh toán; không tự duyệt.
- Không tự đổi giá hoặc Publish sản phẩm.
- Không thực hiện refund.

### Admin

- Tạo tài khoản Staff/Admin và khóa/mở tài khoản.
- Đặt giá SKU và Publish sản phẩm.
- Duyệt/từ chối điều chỉnh giảm tồn.
- Quản lý và hủy Flash Sale.
- Duyệt hủy đơn PayOS đã thu tiền.
- Đối soát `LATE_PAID`, thu dư và sai lệch số tiền.
- Ghi nhận refund thủ công toàn phần và chứng từ.
- Xem audit và báo cáo cơ bản.

### Quy tắc phân quyền bắt buộc

- Một tài khoản có đúng một role: `CUSTOMER`, `STAFF` hoặc `ADMIN`.
- Authorization phải kiểm tra ở backend, không dựa vào việc React ẩn nút.
- Customer chỉ được truy cập dữ liệu thuộc chính mình.
- Hệ thống/Worker là actor kỹ thuật, không phải role người dùng.
- Endpoint webhook PayOS public về mạng nhưng không public về độ tin cậy; phải xác minh chữ ký và dữ liệu.
- Quyền gắn `[DX]` không được tự mở trước khi có quyết định.

### Quyền còn mở

Các quyền PM-Q01/02/03/04/07/08/10 vẫn **PROPOSED**:

- Staff quản lý danh mục hoặc sửa nội dung/hình ảnh Published.
- Admin có được làm thay thao tác vận hành hằng ngày hay chỉ đọc/can thiệp ngoại lệ.
- Bắt buộc phân tách người tạo và người duyệt điều chỉnh tồn.
- Staff xem toàn bộ đơn và mức che thông tin khách hàng.
- Phạm vi dashboard/báo cáo Staff.
- Tra cứu đơn của tài khoản bị khóa.
- Quyền sửa SKU/ẩn/ngừng bán/đưa Published về Draft.

---

## 6. Mười module nghiệp vụ

| Module | Phạm vi | Nội dung |
|---|---|---|
| M01 Account & Access | Đầy đủ | Tài khoản, đăng nhập, hồ sơ, địa chỉ, role cố định, khóa tài khoản |
| M02 Product & Catalog | Đầy đủ | Category, Product, SKU, hình ảnh, giá, Draft/Published/Archived |
| M03 Inventory | Đầy đủ | Tồn vật lý/khả dụng/giữ/phân bổ, movement, điều chỉnh có duyệt |
| M04 Cart | Đầy đủ | Giỏ Guest client-only, giỏ Customer server-side, merge và validate |
| M05 Order & Checkout | Đầy đủ | Checkout, snapshot, idempotency, vòng đời đơn, hủy và expiry |
| M06 Payment | Đầy đủ | PayOS, COD, attempts, callback, transaction, reconciliation và refund |
| M07 Flash Sale | Đầy đủ | Campaign, offer, giá sale, quota, per-customer limit |
| M08 Shipping | Mỏng | Đóng gói, giao mô phỏng, retry và hoàn kho |
| M09 Notification | Đã có MVP | Feed/read owner-only, event-key idempotency; chưa Outbox/push realtime |
| M10 Reporting & Audit | Đã có MVP | Revenue VERIFIED, low-stock cấu hình, Staff tasks và Admin audit query |

---

## 7. Business invariants bắt buộc

### Account và Catalog

- Tài khoản phải `ACTIVE` mới đăng nhập và tạo đơn mới.
- Chỉ SKU thuộc Product `PUBLISHED` và SKU `ACTIVE` mới được checkout.
- Staff soạn Draft; Admin quản lý giá và Publish.
- Giá trong đơn phải là snapshot tại lúc transaction checkout thành công.
- Thay đổi sản phẩm, SKU, giá hoặc địa chỉ sau đó không làm thay đổi đơn cũ.

### Inventory

Mô hình hiện tại dùng:

```text
PhysicalQty = AvailableQty + ReservedQty + AllocatedQty
```

Tất cả thành phần phải không âm.

- `AvailableQty`: còn có thể bán.
- `ReservedQty`: đang giữ cho đơn chưa xác nhận thanh toán/COD.
- `AllocatedQty`: đã xác nhận cho đơn nhưng chưa xuất kho.
- Khi xuất kho, Reservation chuyển `CONSUMED` và hàng rời phân vùng tồn.
- Khi hàng giao thất bại thực tế quay về kho, tạo movement `RETURN_IN`; không release Reservation đã `CONSUMED`.
- Nhập tăng được Staff thực hiện trực tiếp.
- Giảm tồn/kiểm kê lệch phải có lý do và Admin phê duyệt trước khi có hiệu lực.

### Checkout

- Checkout nhiều SKU là All-or-Nothing.
- Không tin giá, tổng tiền, status hoặc quyền do client gửi.
- Order chỉ được trả về thành công sau khi transaction DB commit.
- Giỏ không giữ hàng.
- Idempotency được xác định theo `(customerId, idempotencyKey)` và hash payload chuẩn hóa.
- Cùng key/cùng payload trả kết quả đã commit; cùng key/khác payload trả 409.
- Một request thất bại không được để lại Order, Reservation hoặc quota giữ một phần.

### COD

- Order dùng tên chuẩn `PENDING_COD_CONFIRMATION`.
- Payment khởi tạo `COD_PENDING`, không dùng `PENDING` PayOS.
- Tối đa hai đơn COD đang chờ xác nhận trên mỗi Customer.
- Hạn Staff xác nhận là 24 giờ.
- Quá hạn phải hủy và nhả hàng đúng một lần.
- `COD_COLLECTED` chỉ được ghi cùng lúc với `DELIVERED` khi xác nhận thực thu.
- Payment COD bị hủy hoặc hết hạn trước khi thu chuyển `COD_PENDING -> VOID`; `VOID` mang nghĩa chung “nghĩa vụ thanh toán đã đóng, chưa có khoản thu được xác minh” theo [ADR 0002](docs/adr/0002-checkout-idempotency-and-cod-void.md).

### PayOS

- Order PayOS giữ hàng 15 phút từ lúc tạo thành công.
- Link PayOS phải dùng cùng mốc hết hạn với Order/Reservation.
- Tối đa ba attempts trong thời hạn, mỗi attempt có provider order code riêng.
- Redirect/return URL không được dùng để đánh dấu đã thu tiền.
- Webhook phải xác minh signature, order code, amount và attempt.
- Callback lặp của cùng provider transaction chỉ có một hiệu lực.
- Khoản thu thật từ attempt khác phải được lưu, kể cả khi là khoản thu dư.
- Không mở transaction SQL trong lúc gọi PayOS.
- Khi Order đã `EXPIRED`/`CANCELLED`, tiền xác minh sau đó là `LATE_PAID`; không hồi sinh Order hoặc giữ lại kho.
- Refund MVP là thủ công, toàn phần, do Admin xác nhận cùng evidence; không gọi refund API PayOS.

### Flash Sale

- Flash Sale hiện cho phép COD theo [ADR 0004](docs/adr/0004-enable-cod-for-flash-sale-while-payos-deferred.md); PayOS vẫn deferred theo ADR 0003.
- Offer gắn một SKU, giá sale, quota và giới hạn mua.
- Giữ quota và giữ tồn trong cùng transaction.
- `QuotaHeld + QuotaSold <= QuotaTotal`.
- Mặc định `PerCustomerHeld + PerCustomerBought <= 2` cho mỗi SKU/Customer/Campaign.
- Hết tồn, hết quota, ngoài thời gian hoặc vượt giới hạn đều từ chối.
- Admin hủy campaign `ACTIVE` chỉ chặn checkout mới.
- Đơn đã HELD/PAID giữ giá và deadline cũ; không tự hủy hoặc refund hàng loạt.
- Quota HELD được trả khi đơn hủy/hết hạn.
- Quota SOLD chỉ trả khi đơn hủy hợp lệ trước xuất kho và campaign vẫn `ACTIVE`.
- Hosted lifecycle worker tuần tự hóa và lưu `SCHEDULED -> ACTIVE -> ENDED`; checkout đồng thời vẫn đánh giá hiệu lực theo clock để không phụ thuộc polling delay.

### Shipping và hoàn kho

- Shipping chỉ có năm trạng thái: `PENDING_PACKING`, `PACKED`, `SHIPPING`, `DELIVERED`, `DELIVERY_FAILED`.
- Hàng rời kho khi chuyển sang `SHIPPING`; Reservation chuyển `CONSUMED`.
- Không hủy trực tiếp sau `SHIPPING`.
- Sau thất bại được giao lại tối đa hai lần, không kể lần giao đầu.
- Không được vừa retry vừa hoàn kho.
- Hoàn kho chỉ sau khi thực nhận hàng.
- `ReturnStockCompleted` chỉ được chuyển `false -> true` một lần.
- Khi hoàn kho: ghi `RETURN_IN`, Order `CANCELLED` với lý do `DELIVERY_FAILED_RETURNED`; PayOS đã thu thì `REFUND_REQUIRED`.

### Audit

Phải audit các thao tác nhạy cảm tối thiểu:

- Tạo/khóa/mở tài khoản nội bộ.
- Thay đổi giá, Publish hoặc thay đổi nội dung Published.
- Nhập kho và quyết định điều chỉnh giảm.
- Chuyển trạng thái đơn/giao hàng quan trọng.
- Duyệt hủy đơn đã thanh toán.
- Đối soát và refund thủ công.
- Tạo/hủy Flash Sale.

Audit cần actor, actor type, action, entity, thời điểm, correlation ID và before/after phù hợp; không ghi secret hoặc dữ liệu thanh toán không cần thiết.

---

## 8. State models

### 8.1 Order

Các trạng thái chuẩn:

```text
PENDING_PAYMENT
PENDING_COD_CONFIRMATION
CONFIRMED
FULFILLING
COMPLETED
CANCELLED
EXPIRED
```

Các chuyển chính:

```text
PayOS:
NEW -> PENDING_PAYMENT
PENDING_PAYMENT -> CONFIRMED | EXPIRED | CANCELLED

COD:
NEW -> PENDING_COD_CONFIRMATION
PENDING_COD_CONFIRMATION -> CONFIRMED | CANCELLED

Fulfillment:
CONFIRMED -> FULFILLING
FULFILLING -> COMPLETED
CONFIRMED/FULFILLING -> CANCELLED (chỉ trước SHIPPING và chưa CONSUMED)
FULFILLING -> CANCELLED (DELIVERY_FAILED_RETURNED sau khi hàng về kho)
```

Điều kiện quan trọng:

- `PENDING_PAYMENT -> CONFIRMED/EXPIRED/CANCELLED` phải là guarded transition; một commit thắng.
- Không cho `EXPIRED/CANCELLED -> CONFIRMED` chỉ vì callback đến muộn.
- `COMPLETED` chỉ khi Shipment `DELIVERED` và Payment `SUCCESS` hoặc `COD_COLLECTED`.

### 8.2 Payment

PayOS:

```text
PENDING
FAILED
SUCCESS
VOID
LATE_PAID
REFUND_REQUIRED
REFUNDED
```

COD:

```text
COD_PENDING
COD_COLLECTED
```

Luồng chính:

```text
PENDING/FAILED -> SUCCESS
PENDING/FAILED -> VOID
VOID/PENDING/FAILED -> LATE_PAID
SUCCESS -> REFUND_REQUIRED -> REFUNDED
LATE_PAID -> REFUND_REQUIRED -> REFUNDED
COD_PENDING -> COD_COLLECTED
COD_PENDING -> VOID
VOID -> LATE_PAID (chỉ khi phát hiện khoản thu thật muộn; tạo reconciliation, không hồi sinh Order)
```

`PaymentAttempt` có vòng đời riêng: `CREATED`, `LINK_READY`, `FAILED`, `SUCCESS`, `EXPIRED`. Attempt thất bại không tự động quyết định Order thất bại.

### 8.3 Reservation

```text
HELD -> ALLOCATED -> CONSUMED
HELD -> RELEASED
ALLOCATED -> RELEASED
```

- `HELD -> RELEASED` khi đơn hết hạn/hủy trước xác nhận.
- `ALLOCATED -> RELEASED` khi hủy hợp lệ trước xuất kho.
- `CONSUMED` không được release lại; hàng trả về là movement độc lập.

### 8.4 Flash Sale Campaign và Allocation

Campaign:

```text
DRAFT -> SCHEDULED -> ACTIVE -> ENDED
DRAFT/SCHEDULED/ACTIVE -> CANCELLED
```

Allocation:

```text
HELD -> SOLD
HELD -> RELEASED
SOLD -> RELEASED chỉ theo chính sách hủy trước xuất kho khi campaign ACTIVE
```

### 8.5 Shipment

```text
PENDING_PACKING -> PACKED -> SHIPPING -> DELIVERED
                                  |
                                  -> DELIVERY_FAILED
DELIVERY_FAILED -> SHIPPING (retry tối đa 2)
DELIVERY_FAILED + hàng thực nhận -> ReturnStockCompleted=true
```

---

## 9. Các quyết định SM-Q01 đến SM-Q09 đã chốt

1. **SM-Q01 — Payment vs Expiry:** guarded transition trên `PENDING_PAYMENT`; Worker kiểm tra PayOS một lần tại hạn, không grace period; commit đầu tiên thắng; tiền đến sau là `LATE_PAID`.
2. **SM-Q02 — Cancel Flash Sale ACTIVE:** chỉ dừng checkout mới; đơn HELD/PAID tiếp tục theo snapshot.
3. **SM-Q03 — Delivery failed:** giao lại tối đa hai lần; hoàn kho thực nhận tối đa một lần bằng `ReturnStockCompleted`.
4. **SM-Q04 — Hủy PayOS đã trả:** Staff gửi yêu cầu, Admin quyết định; chỉ hủy trực tiếp trước `SHIPPING` và trước khi Reservation `CONSUMED`.
5. **SM-Q05 — Refund:** refund thủ công toàn phần; Admin lưu evidence trước khi chuyển `REFUNDED`.
6. **SM-Q06 — Trả kho/quota:** trả allocated stock một lần; quota SOLD chỉ mở lại khi campaign còn `ACTIVE`.
7. **SM-Q07 — COD hoàn tất:** ghi `COD_COLLECTED` cùng `DELIVERED`; Order hoàn tất khi cả hàng và tiền hợp lệ.
8. **SM-Q08 — Payment attempts:** tối đa ba attempts trong 15 phút; mọi khoản thực thu phải được ghi nhận và đối soát.
9. **SM-Q09 — All-or-Nothing:** giữ toàn bộ SKU/quota trong một transaction; một điều kiện sai thì rollback toàn bộ.

---

## 10. Kiến trúc mục tiêu

### 10.1 Modular Monolith

Backend ban đầu là một deployable ASP.NET Core API, chia ranh giới module ở mức code. Không tách microservices trong MVP vì Inventory, Order, Payment và Flash Sale cần transaction PostgreSQL cục bộ.

```text
React
  |
ASP.NET Core API
  ├── Api: HTTP, auth boundary, request/response
  ├── Application: use cases, commands/queries, transaction orchestration
  ├── Domain: state rules, invariants, domain concepts
  └── Infrastructure: EF/Npgsql, PayOS, workers, cache, messaging
       |
   PostgreSQL (authority)
```

### 10.2 Trách nhiệm project

#### HighConcurrencyCommerce.Api

- Controllers/endpoints.
- Authentication/authorization policies.
- Validation ở HTTP boundary.
- Error mapping và correlation ID.
- Dependency injection và middleware.
- Không chứa transaction nghiệp vụ dài hoặc tự sửa entity trực tiếp.

#### HighConcurrencyCommerce.Application

- Commands, queries, DTO và handlers.
- Ownership/permission checks ở use case.
- Transaction coordination.
- Idempotency workflow.
- State transition services.
- Interface cho persistence, PayOS, clock, current user và event publishing.

#### HighConcurrencyCommerce.Domain

- Business states và rule có thể biểu diễn độc lập.
- Guard hợp lệ cho Order/Payment/Reservation/Shipment/Flash Sale.
- Value object cho tiền, quantity, identifiers nếu áp dụng.
- Không phụ thuộc EF, React, Redis, RabbitMQ hoặc PayOS SDK.

#### HighConcurrencyCommerce.Infrastructure

- `StoreDbContext`, EF mappings và repository/query implementation.
- Transaction/locking SQL cho đường nhạy cảm.
- PayOS client và signature verification.
- Worker, Outbox, cache và messaging nếu được duyệt.
- Configuration adapters.

### 10.3 PostgreSQL là source of truth

- Tồn kho, quota, Order và Payment được quyết định tại PostgreSQL transaction.
- Redis nếu dùng chỉ là read cache; không được là nguồn duy nhất cho tồn hoặc limit.
- RabbitMQ nếu dùng chỉ nhận event sau commit qua Outbox.
- React không quyết định giá, status, quyền hoặc thành công giao dịch.
- API instances không giữ state nghiệp vụ quan trọng trong RAM.

### 10.4 Thành phần chưa bắt buộc

Redis, RabbitMQ, Transactional Outbox, lịch Worker và topology deploy vẫn là **PROPOSED/VERIFY**. Auth đã chốt cookie/session theo ADR 0001; không dùng JWT ở MVP.

---

## 11. Transaction và concurrency patterns

### 11.1 Checkout All-or-Nothing

Thứ tự khóa chuẩn để giảm deadlock:

```text
Customer -> FlashSaleOffer theo ID -> InventoryBalance theo SKU
```

Pseudoflow:

1. Xác thực Customer `ACTIVE`, ownership địa chỉ và request.
2. Chuẩn hóa payload và tính hash.
3. `BEGIN`.
4. Khóa Customer để serialize COD pending limit và luồng cùng tài khoản.
5. Đọc/khóa `CheckoutRequest` theo `(CustomerId, IdempotencyKey)`.
6. Nếu đã `SUCCEEDED`, trả snapshot cũ; khác payload thì conflict.
7. Khóa Offer theo ID ổn định, rồi Inventory theo SKU ổn định.
8. Kiểm tra publication, SKU, giá, tồn, quota và per-customer limit.
9. Với COD, đếm đơn `PENDING_COD_CONFIRMATION`; từ chối đơn thứ ba.
10. Tạo Order, Item snapshot, Payment, Reservation/Lines và Allocation.
11. Cập nhật tồn và quota bằng guard.
12. Lưu kết quả CheckoutRequest.
13. `COMMIT` rồi mới trả success.
14. Bất kỳ lỗi nào trước commit phải rollback toàn bộ.

### 11.2 PayOS callback vs Worker expiry

1. Webhook xác minh signature, amount, provider reference và attempt.
2. Lưu callback receipt/idempotency evidence.
3. Lưu `PaymentTransaction` nếu có khoản thực thu thật.
4. Callback thử guarded update `PENDING_PAYMENT -> CONFIRMED` cùng side effects.
5. Worker tại deadline kiểm tra PayOS một lần rồi thử `PENDING_PAYMENT -> EXPIRED` cùng release/VOID.
6. Chỉ một guarded update có affected row bằng 1.
7. Nhánh thua đọc trạng thái cuối và không lặp side effect.
8. Nếu có tiền sau khi Order đã đóng, tạo reconciliation; không re-allocate kho.

### 11.3 Return stock at most once

1. Yêu cầu Shipment `DELIVERY_FAILED` và có bằng chứng hàng đã về.
2. Guard `return_stock_completed = false`.
3. Nhánh thắng cập nhật cờ, tăng physical/available, ghi `RETURN_IN`, đóng Order.
4. Nhánh thua không có side effect.
5. Không release Reservation `CONSUMED`.

### 11.4 Guarded update

Với các transition cạnh tranh, không dùng pattern “đọc rồi ghi” thiếu điều kiện. Cần update có điều kiện và kiểm tra affected rows, ví dụ về ý tưởng:

```sql
UPDATE store.sales_orders
SET order_status = 'CONFIRMED', updated_at = now()
WHERE id = @order_id
  AND order_status = 'PENDING_PAYMENT';
```

Transition và toàn bộ side effects liên quan phải nằm trong cùng transaction; đoạn SQL trên chỉ minh họa guard, không phải toàn bộ workflow.

---

## 12. Mô hình dữ liệu E01–E31

Schema PostgreSQL: `store`. Các ID là UUID. Tiền lưu `numeric(18,2)`; API nên dùng VND integer/decimal, tuyệt đối không dùng `float`/`double` cho tính tiền.

| ID | Entity / bảng | Trách nhiệm chính |
|---|---|---|
| E01 | `UserAccount` / `user_accounts` | Danh tính, role, trạng thái tài khoản |
| E02 | `Role` / `roles` | CUSTOMER, STAFF, ADMIN |
| E03 | `CustomerAddress` / `customer_addresses` | Địa chỉ thuộc Customer, một địa chỉ mặc định |
| E04 | `Category` / `categories` | Danh mục sản phẩm |
| E05 | `Product` / `products` | Nội dung và publication state |
| E06 | `ProductSku` / `product_skus` | Biến thể, SKU code, regular price, status |
| E07 | `ProductImage` / `product_images` | Ảnh, file key, trạng thái và thứ tự |
| E08 | `InventoryBalance` / `inventory_balances` | Physical/available/reserved/allocated và revision |
| E09 | `InventoryMovement` / `inventory_movements` | Ledger nhập/giữ/nhả/phân bổ/xuất/trả/điều chỉnh |
| E10 | `InventoryAdjustmentRequest` / `inventory_adjustment_requests` | Yêu cầu giảm tồn và phê duyệt |
| E11 | `StockReservation` / `stock_reservations` | Reservation 1-1 với Order |
| E12 | `StockReservationLine` / `stock_reservation_lines` | Reservation theo OrderItem/SKU |
| E13 | `ShoppingCart` / `shopping_carts` | Giỏ server của Customer |
| E14 | `CartItem` / `cart_items` | SKU và quantity trong giỏ |
| E15 | `SalesOrder` / `sales_orders` | Order, payment method, status, deadline và snapshot địa chỉ |
| E16 | `SalesOrderItem` / `sales_order_items` | SKU/title/option/price snapshot và quantity |
| E17 | `CheckoutRequest` / `checkout_requests` | Idempotency key, payload hash và response snapshot |
| E18 | `OrderCancellationRequest` / `order_cancellation_requests` | Yêu cầu và quyết định hủy |
| E19 | `Payment` / `payments` | Payment aggregate theo Order |
| E20 | `PaymentAttempt` / `payment_attempts` | Tối đa ba lần tạo link PayOS |
| E21 | `PaymentTransaction` / `payment_transactions` | Mỗi khoản thực thu đã xác minh |
| E22 | `ProviderCallbackEvent` / `provider_callback_events` | Mỗi callback receipt và kết quả xử lý |
| E23 | `PaymentReconciliationCase` / `payment_reconciliation_cases` | Hồ sơ LATE_PAID/thu trùng/sai tiền |
| E24 | `ManualRefund` / `manual_refunds` | Refund thủ công và evidence |
| E25 | `FlashSaleCampaign` / `flash_sale_campaigns` | Vòng đời chương trình sale |
| E26 | `FlashSaleOffer` / `flash_sale_offers` | SKU, sale price, quota, limit |
| E27 | `FlashSaleAllocation` / `flash_sale_allocations` | HELD/SOLD/RELEASED theo OrderItem |
| E28 | `Shipment` / `shipments` | Shipping state, retry và return guard |
| E29 | `DeliveryAttempt` / `delivery_attempts` | Từng lần giao |
| E30 | `InAppNotification` / `in_app_notifications` | Thông báo theo recipient |
| E31 | `AuditLog` / `audit_logs` | Nhật ký actor/action/entity/before/after |

### Constraint/index quan trọng đã có ở database/model mapping

- Email unique không phân biệt hoa thường ở DB.
- Một địa chỉ mặc định cho mỗi Customer.
- Một InventoryBalance cho mỗi SKU.
- Một Cart cho mỗi Customer; một SKU một dòng trong Cart.
- Order number unique.
- Một CheckoutRequest idempotency key trên mỗi Customer.
- Một Reservation, Payment và Shipment trên mỗi Order.
- Một cancellation request `PENDING` trên mỗi Order.
- Attempt number unique trong Payment, từ 1 đến 3.
- Provider transaction identity unique khi có giá trị.
- Offer unique theo Campaign/SKU.
- Allocation unique theo OrderItem.
- Delivery attempt unique theo Shipment/attempt number.
- Index cho expiry, COD pending, reconciliation, callback, quota, notification và audit.

### Invariant liên bảng phải thực hiện ở service/integration test

- ShippingAddress phải thuộc Customer của Order.
- Order và Payment phải cùng payment method.
- ReservationLine và OrderItem phải cùng Order và SKU.
- FlashSale Allocation, Offer và OrderItem phải cùng SKU.
- Refund phải tương ứng khoản thực thu và đúng chính sách toàn phần.
- Customer role/status phải hợp lệ cho thao tác.
- Per-customer Flash Sale limit và COD pending limit phải đúng dưới concurrency.

---

## 13. API conventions mục tiêu

- Base path: `/api/v1`.
- HTTPS ở staging/production.
- JSON UTF-8.
- Timestamp ISO-8601 UTC.
- Tiền không dùng số thực nhị phân.
- Client không truyền trực tiếp Order/Payment/Reservation status; dùng command endpoint.
- Checkout bắt buộc header `Idempotency-Key`.
- Auth dùng opaque session cookie HttpOnly + PostgreSQL theo ADR 0001; CSRF header bắt buộc cho mutation.
- Ownership và role kiểm tra ở backend.
- Webhook dùng provider authentication/signature, không dùng Customer JWT.
- Error response cần code ổn định, message an toàn và correlation ID.

HTTP status gợi ý:

| Status | Ý nghĩa |
|---|---|
| 200 | GET/command thành công hoặc idempotency replay |
| 201 | Tạo resource/checkout thành công |
| 400 | Payload/validation sai |
| 401 | Chưa xác thực hoặc token không hợp lệ |
| 403 | Không có quyền hoặc không sở hữu dữ liệu |
| 404 | Không tìm thấy hoặc không được phép biết resource tồn tại |
| 409 | State conflict, idempotency conflict, hết tồn/quota hoặc race thua |
| 422 | Payload đúng cú pháp nhưng vi phạm rule nghiệp vụ phù hợp |
| 429 | Rate limit |

Các error code nên có tối thiểu: `VALIDATION_ERROR`, `UNAUTHORIZED`, `FORBIDDEN`, `NOT_FOUND`, `INVALID_STATE_TRANSITION`, `IDEMPOTENCY_KEY_REUSED`, `CHECKOUT_IN_PROGRESS`, `OUT_OF_STOCK`, `FLASH_SALE_QUOTA_EXCEEDED`, `FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED`, `COD_PENDING_LIMIT_EXCEEDED`, `PAYMENT_ATTEMPT_LIMIT_EXCEEDED`, `PAYMENT_EXPIRED`, `CONCURRENCY_CONFLICT`.

---

## 14. Catalog 68 API dự kiến

Các route dưới đây là **thiết kế tổng thể**. Ba GET catalog đã implement (regular price, chưa có ảnh/media URL và giá Flash Sale); auth register/login/logout/me, Admin account status và Customer profile/addresses (T11) đã implement. ADR 0001 bổ sung GET `/auth/csrf` và POST `/auth/password`. Các route khác chưa implement. Contract chạy thật: [catalog](docs/api/catalog.md), [auth](docs/api/auth.md), [account](docs/api/account.md). Các `[DX]` ngoài me/logout vẫn cần quyết định riêng.

### M01 — Account & Access (12)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| POST | `/auth/register` | Guest | Đăng ký Customer |
| POST | `/auth/login` | Guest | Đăng nhập tài khoản ACTIVE |
| POST | `/auth/logout` | Authenticated (ADR 0001) | Đăng xuất phiên hiện tại, đã implement |
| GET | `/auth/me` | Authenticated (ADR 0001) | Xem tài khoản hiện tại, đã implement |
| PATCH | `/account/profile` | Customer `[DX]` | Sửa hồ sơ của mình |
| GET | `/account/addresses` | Customer | Danh sách địa chỉ |
| GET | `/account/addresses/{id}` | Customer | Xem chi tiết địa chỉ thuộc mình |
| POST | `/account/addresses` | Customer | Tạo địa chỉ |
| PATCH | `/account/addresses/{id}` | Customer | Sửa địa chỉ thuộc mình |
| DELETE | `/account/addresses/{id}` | Customer | Xóa địa chỉ thuộc mình |
| POST | `/admin/staff-accounts` | Admin | Tạo Staff/Admin |
| PATCH | `/admin/accounts/{id}/status` | Admin | Khóa/mở tài khoản |

### M02 — Product & Catalog (10)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/catalog/categories` | Public | Danh mục công khai |
| GET | `/catalog/products` | Public | Tìm/lọc/phân trang sản phẩm |
| GET | `/catalog/products/{id}` | Public | Chi tiết Product/SKU |
| POST | `/staff/products` | Staff | Tạo Draft |
| PATCH | `/staff/products/{id}/draft` | Staff | Sửa Draft |
| POST | `/staff/products/{id}/skus` | Staff | Thêm SKU cho Draft |
| PATCH | `/admin/skus/{id}/price` | Admin | Cập nhật giá |
| POST | `/admin/products/{id}/publish` | Admin | Publish sản phẩm đủ điều kiện |
| PATCH | `/staff/products/{id}/published-content` | Staff `[DX]` | Sửa nội dung/ảnh Published có audit |
| POST | `/staff/categories` | Staff `[DX]` | Tạo danh mục |

### M03 — Inventory (6)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/staff/inventory` | Staff | Xem tồn theo SKU |
| GET | `/staff/inventory/{skuId}/movements` | Staff `[DX]` | Lịch sử biến động |
| POST | `/staff/inventory/stock-in` | Staff | Nhập tăng |
| POST | `/staff/inventory/adjustment-requests` | Staff | Yêu cầu giảm/kiểm kê lệch |
| GET | `/admin/inventory/adjustment-requests` | Admin | Xem yêu cầu |
| POST | `/admin/inventory/adjustment-requests/{id}/decision` | Admin | Duyệt/từ chối |

### M04 — Cart (5)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/cart` | Customer | Xem giỏ |
| PUT | `/cart/items/{skuId}` | Customer | Thêm/cập nhật quantity |
| DELETE | `/cart/items/{skuId}` | Customer | Xóa SKU |
| POST | `/cart/merge` | Customer | Gộp giỏ Guest |
| POST | `/cart/validate` | Customer | Validate trước checkout, không giữ hàng |

### M05 — Order & Checkout (9)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| POST | `/checkouts` | Customer | Checkout COD/PayOS nguyên khối |
| GET | `/orders` | Customer | Danh sách đơn của mình |
| GET | `/orders/{id}` | Customer | Chi tiết đơn của mình |
| POST | `/orders/{id}/cancel` | Customer | Hủy/yêu cầu hủy hợp lệ |
| GET | `/staff/orders` | Staff `[DX]` | Tra cứu đơn cần xử lý |
| POST | `/staff/orders/{id}/confirm-cod` | Staff | Xác nhận COD trong 24 giờ |
| POST | `/staff/orders/{id}/start-fulfillment` | Staff | Tiếp nhận đơn xác nhận |
| POST | `/staff/orders/{id}/cancellation-requests` | Staff | Đề nghị hủy PayOS đã thu |
| POST | `/admin/order-cancellations/{id}/decision` | Admin | Duyệt/từ chối hủy |

### M06 — Payment (8)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/orders/{id}/payment` | Customer | Trạng thái payment của đơn mình |
| POST | `/orders/{id}/payment-attempts` | Customer | Tạo attempt tiếp theo khi còn hạn |
| GET | `/orders/{id}/payment-attempts` | Customer | Xem attempts |
| POST | `/integrations/payos/webhook` | PayOS | Webhook đã xác minh |
| GET | `/admin/payments/reconciliation` | Admin | Danh sách cần đối soát |
| GET | `/admin/payments/reconciliation/{id}` | Admin | Chi tiết hồ sơ |
| POST | `/admin/refunds/{id}/mark-refunded` | Admin | Ghi nhận refund thủ công |
| GET | `/staff/orders/{id}/payment-summary` | Staff `[DX]` | Tóm tắt payment |

### M07 — Flash Sale (8)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/flash-sales` | Public | Danh sách sale công khai |
| GET | `/flash-sales/{id}` | Public | Chi tiết sale/offers |
| POST | `/admin/flash-sales` | Admin | Tạo Draft |
| PATCH | `/admin/flash-sales/{id}` | Admin | Sửa trước mở bán |
| POST | `/admin/flash-sales/{id}/offers` | Admin | Cấu hình SKU/giá/quota |
| POST | `/admin/flash-sales/{id}/publish` | Admin | Publish campaign |
| POST | `/admin/flash-sales/{id}/cancel` | Admin | Dừng checkout mới |
| GET | `/admin/flash-sales/{id}/performance` | Admin | Báo cáo sale cơ bản |

### M08 — Shipping (6)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/staff/shipments` | Staff | Danh sách giao hàng mô phỏng |
| POST | `/staff/shipments/{id}/pack` | Staff | Xác nhận đóng gói |
| POST | `/staff/shipments/{id}/handover` | Staff | Chuyển SHIPPING/xuất kho |
| POST | `/staff/shipments/{id}/delivery-result` | Staff | Ghi DELIVERED/DELIVERY_FAILED |
| POST | `/staff/shipments/{id}/retry` | Staff | Giao lại tối đa hai lần |
| POST | `/staff/shipments/{id}/return-stock` | Staff | Hoàn kho sau thực nhận |

### M09 — Notification (2)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/notifications` | Authenticated | Thông báo của mình |
| POST | `/notifications/{id}/read` | Authenticated `[DX]` | Đánh dấu đã đọc |

### M10 — Reporting & Audit (4)

| Method | Path | Role | Mục đích |
|---|---|---|---|
| GET | `/admin/reports/revenue` | Admin | Báo cáo doanh thu đơn giản |
| GET | `/admin/reports/low-stock` | Admin | Tồn dưới ngưỡng |
| GET | `/admin/audit-logs` | Admin | Tra cứu audit |
| GET | `/staff/dashboard/tasks` | Staff | Tổng hợp hàng đợi tác vụ vận hành |

Endpoint Staff task summary được chốt ở T28 như API v1.1 và bổ sung thêm pack/handover/delivery-failed/low-stock để phản ánh đầy đủ hàng đợi hiện có.

---

## 15. Security baseline

- HTTPS ở môi trường ngoài local.
- Password chỉ lưu dưới dạng hash bằng thuật toán password hashing phù hợp; không tự mã hóa reversible.
- Secret PayOS, DB, Redis/RabbitMQ nằm trong secret store hoặc environment variables.
- Không commit secret, connection string thật hoặc provider payload nhạy cảm.
- Authentication đã có expiry 7 ngày tuyệt đối, revoke logout/current-session và password/role/status/all-sessions.
- Cookie HttpOnly với Secure production, SameSite=Lax và ASP.NET antiforgery header; không dùng JWT ở MVP.
- CORS chỉ cho phép origin cần thiết.
- Validate input ở API boundary và kiểm tra business rule trong application/domain.
- Login/register/password có rate limit theo ADR 0001. Checkout, payment attempt và webhook chưa chốt ngưỡng.
- Webhook xác minh HMAC SHA-256 theo hợp đồng PayOS thực tế.
- Không log password, token, checksum key, connection string hoặc full sensitive payload.
- Query Customer luôn có ownership filter.
- Không mass-assign entity từ request DTO.

---

## 16. Observability và vận hành

Nên có:

- Correlation ID xuyên request, DB audit và event.
- Structured logging.
- Health check tách liveness/readiness.
- Metrics: request count/latency/error, checkout rejection, out-of-stock, deadlock/serialization retry, invalid/duplicate webhook, `LATE_PAID`, open reconciliation, Worker lag và pending outbox.
- Alert cho Worker dừng, callback invalid tăng, reconciliation tồn lâu và DB unavailable.
- Retry DB chỉ cho lỗi transient/deadlock với giới hạn; không retry mù một command không idempotent.
- Backup/restore PostgreSQL được kiểm thử.
- Migration chạy có kiểm soát, không dùng `EnsureCreated` cho production.

---

## 17. Chiến lược kiểm thử

### Unit tests

- State transition hợp lệ/không hợp lệ.
- Tính total và snapshot.
- COD limit/Flash Sale limit ở mức rule thuần.
- Refund policy.
- Permission/ownership policy.

### Integration tests với PostgreSQL thật

- FK/check/index/unique thực tế.
- Transaction rollback All-or-Nothing.
- Lock order và deadlock retry.
- Cross-table guards.
- Idempotency key replay/conflict.
- Callback receipt và provider transaction uniqueness.
- Return stock exactly once.
- Migration up/down hoặc forward-only policy.

Không dùng InMemory provider để chứng minh concurrency PostgreSQL.

### Acceptance concurrency bắt buộc

#### AC-20a — Không oversell

- SKU còn 10.
- 100 request hợp lệ đồng thời, mỗi request mua 1.
- Kỳ vọng đúng 10 checkout/HELD thành công, 90 hết hàng, oversell bằng 0.

**Trạng thái:** PASS ngày 30/10/2026 — 100 Customer/session/address/idempotency key độc lập, POST được đồng bộ bằng start gate; kết quả 10/90 và inventory cuối đã được kiểm tra trực tiếp. Xem [ma trận T29](docs/superpowers/plans/2026-10-30-concurrency-acceptance.md).

#### AC-20b — Callback trùng

- Gửi cùng callback/provider transaction 5 lần.
- Kỳ vọng đúng một tác dụng Payment/Order/Allocation và không có transaction tài chính trùng.

**Trạng thái:** DEFERRED theo ADR 0003; chưa có webhook/signature sandbox thật.

#### AC-20c — Hủy và thanh toán cạnh tranh

- Cho cancel/expiry và payment cùng chạy.
- Kỳ vọng đúng một Order state cuối; tồn/quota không nhả hai lần; tiền thật luôn được ghi và đối soát.

**Trạng thái:** DEFERRED theo ADR 0003; không giả lập provider callback để tuyên bố pass.

### Các case quan trọng khác

- Hai request cùng idempotency key/cùng payload.
- Cùng key/khác payload.
- Request đầu đang `PROCESSING`.
- Multi-SKU thiếu đúng một SKU.
- 100 request tranh quota nhỏ hơn tồn.
- Hai đơn COD tạo đồng thời khi Customer đã có một đơn pending.
- Callback sau `EXPIRED`.
- Hai attempts đều thực thu.
- Delivery return endpoint gọi lặp.
- Flash Sale bị cancel trong lúc checkout.
- Admin duyệt adjustment cạnh thay đổi tồn khác.

Benchmark p95/p99, RPS, load/stress/soak thuộc giai đoạn sau và chưa có KPI được phê duyệt.

---

## 18. Các quyết định còn mở

### OPEN-API-01

Chưa chốt endpoint Staff dashboard hay cách tổng hợp dữ liệu từ route hiện hữu.

### OPEN-FLS-01

Chưa chốt cách hiển thị/đối chiếu counter SOLD/RELEASED sau khi campaign `ENDED/CANCELLED` có đơn hoàn.

### OPEN-PAYOS-01

Phải kiểm chứng trên môi trường PayOS:

- Cách đặt `expiredAt` chính xác.
- Canonicalization và signature payload.
- Mapping nhiều attempts.
- Webhook acknowledgment/retry.
- Tra cứu transaction tại deadline.
- Trường hợp thu dư hoặc callback đến muộn.

### OPEN-AUTH-01

Auth đã chốt tại ADR 0001: cookie HttpOnly, PostgreSQL session 7 ngày, revoke và CSRF; me/logout mở cho authenticated. Các quyền `[DX]` khác chưa chốt, không tự mở.

### OPEN-DB-01

Schema nguồn và upgrade auth 002 đã có trong repo; các module được integration test trên PostgreSQL thật. T30 đã chốt quy trình numbered-SQL review/apply/backup/recovery và nguyên tắc không auto-migrate khi startup. Vẫn chưa có EF migration history hoặc migration ledger tự động; mọi schema change mới phải thêm script số mới và fixture tương ứng.

### Các OPEN khác

- Chủ tài liệu/người duyệt cuối.
- Mẫu evidence refund.
- Chính sách dữ liệu cá nhân cụ thể.
- Ngưỡng tồn thấp.
- Cấu trúc báo cáo doanh thu.
- KPI hiệu năng và cấu hình benchmark.

---

## 19. Roadmap triển khai backend đề xuất

Lộ trình dưới đây là nhóm kiến trúc tổng thể. Danh sách công việc thực thi và trạng thái mới nhất nằm tại [docs/TASKS.md](docs/TASKS.md); public catalog T01–T06 và auth T07–T10 đã hoàn tất trong phạm vi ghi rõ tại đó.

### Giai đoạn 0 — Làm sạch nền tảng

1. Xóa placeholder `Class1.cs`.
2. Thêm `.gitignore` chuẩn .NET và README này vào version control.
3. Chốt Database-First hay migration-owned-by-code.
4. Đưa schema/seed/integrity query vào repository nếu chọn quản lý DB cùng source.
5. Thêm test projects và PostgreSQL test fixture.

### Giai đoạn 1 — Foundation

1. Error contract, exception mapping và correlation ID.
2. Validation.
3. Authentication và policy-based authorization.
4. Current user/clock abstractions.
5. Transaction abstraction và integration-test database.
6. Seed roles và tài khoản Admin bootstrap an toàn.

### Giai đoạn 2 — Read-side và quản trị cơ bản

1. Public catalog.
2. Account/profile/address.
3. Product Draft/SKU/price/Publish.
4. Inventory query/stock-in/adjustment approval.
5. Audit nền tảng.

### Giai đoạn 3 — Cart và checkout core

1. Customer cart/merge/validate.
2. Checkout request/idempotency.
3. Order snapshot.
4. Multi-SKU reservation transaction.
5. COD limit và expiry.
6. AC-20a.

### Giai đoạn 4 — PayOS

1. PayOS adapter sau khi VERIFY provider contract.
2. Payment attempts.
3. Webhook signature/receipt/transaction.
4. Worker expiry và guarded race.
5. Reconciliation/refund.
6. AC-20b và AC-20c.

### Giai đoạn 5 — Flash Sale

1. Campaign/Offer administration.
2. Quota hold/sold/release trong checkout transaction.
3. Per-customer limit.
4. Cancel ACTIVE policy.
5. Concurrency test quota.

### Giai đoạn 6 — Fulfillment và module mỏng

1. Shipment states.
2. Delivery attempts/retry.
3. Return stock exactly once.
4. In-app notifications.
5. Revenue/low-stock reports.
6. Audit UI/API.

### Giai đoạn 7 — Hardening

1. Security review.
2. Observability/health checks — T30 hoàn tất phần nền.
3. Backup/migration runbook — T30 hoàn tất; restore drill production còn phụ thuộc hạ tầng thật.
4. Docker/Compose — T30 hoàn tất smoke stack; CI/CD và production manifests còn phụ thuộc nhà cung cấp.
5. Benchmark plan sau khi KPI được chốt.

---

## 20. Definition of Done cho một use case

Một use case chỉ được xem là hoàn thành khi:

- Quyền và ownership được kiểm tra ở server.
- Request DTO được validate; không bind thẳng entity.
- Business state transition được guard.
- Transaction boundary và failure behavior rõ ràng.
- Không trả success trước commit.
- Idempotency được xử lý nếu command có thể retry.
- Audit có mặt nếu là thao tác nhạy cảm.
- Không log secret/sensitive payload.
- Có unit test cho rule và integration test với PostgreSQL.
- Các race quan trọng có test đồng thời.
- OpenAPI/error codes được cập nhật.
- README hoặc ADR được cập nhật nếu có quyết định mới.

---

## 21. Quy ước phát triển cần giữ

- Dùng UTC trong persistence và API; chỉ chuyển timezone ở presentation.
- Dùng `decimal` cho tiền trong C#.
- Status nên được tập trung thành constant/enum có mapping rõ, tránh string rải rác.
- Không cho client set state trực tiếp.
- Không gọi external network trong DB transaction.
- Lock nhiều row theo thứ tự ổn định.
- Không dùng Redis lock thay cho PostgreSQL guard.
- Mọi consumer/event handler phải idempotent vì message có thể được giao lặp.
- Không thêm bảng kỹ thuật vào danh sách E01–E31 mà không phân biệt rõ; Outbox là bảng kỹ thuật tùy chọn.
- Thay đổi schema database đang có dữ liệu phải dùng migration/ALTER có kiểm tra; không chạy create-schema dành cho database rỗng.
- Khi một quyết định OPEN được chốt, cập nhật state model, API, DB guard, test và README cùng lúc.

---

## 22. Checklist trước khi bắt đầu code feature mới

1. Feature thuộc module nào và actor nào?
2. Quyền đã CONFIRMED hay còn `[DX]`?
3. Entity nào sở hữu dữ liệu?
4. State trước/sau là gì?
5. Có cạnh tranh với Worker, callback hoặc request khác không?
6. Transaction bắt đầu/kết thúc ở đâu?
7. Cần lock row nào và theo thứ tự nào?
8. Retry có tạo tác dụng trùng không?
9. Có cần idempotency key/unique identity không?
10. Side effect nào chỉ được xảy ra một lần?
11. Có cần audit/notification/outbox không?
12. Test nào chứng minh rule đúng trên PostgreSQL thật?

---

## 23. Kết luận kỹ thuật

Nền tảng persistence đã có bộ khung tốt với đủ 31 entity và mapping PostgreSQL. Phần quan trọng còn lại không phải CRUD đơn thuần mà là hiện thực đúng transaction, state machine, idempotency và concurrency guard.

Thứ tự ưu tiên khi ra quyết định:

1. Tính đúng của Order/Inventory/Payment/Quota.
2. Phân quyền và ownership.
3. Khả năng đối soát/audit.
4. API và trải nghiệm người dùng.
5. Cache, messaging và tối ưu hiệu năng.

Nếu phải chọn giữa giải pháp “nhanh nhưng chỉ đúng khi chạy một request” và giải pháp có guarded transaction đúng trên nhiều instance, dự án này bắt buộc chọn phương án thứ hai.
