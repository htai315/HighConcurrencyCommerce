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
| Thanh toán | COD đã triển khai; PayOS T19–T22 đã có attempts/webhook/lifecycle, Customer return UI, prepaid fulfillment và Admin reconciliation/manual refund đã harden theo đúng case/transaction; feature vẫn mặc định tắt cho tới khi sandbox E2E callback thật và provider paid-query contract đạt release gate |
| Giao hàng | Vận đơn, nhãn và tracking nội bộ provider-neutral; chưa gọi API/webhook hãng thật |
| Thông báo | In-app trong MVP |
| Kiến trúc backend mục tiêu | Modular Monolith |

### Ngoài phạm vi hiện tại

- Marketplace nhiều người bán, chia hoa hồng hoặc chia tiền.
- Nhiều kho và luân chuyển hàng giữa kho.
- Tích hợp API/webhook hãng vận chuyển thật, mua nhãn tự động, multi-package và SLA đối tác.
- Tự động refund qua API PayOS.
- Livestream, đấu giá, trả góp, loyalty và cá nhân hóa.
- Voucher phức tạp; đổi/trả sau giao hàng COD đã được đưa vào kế hoạch T39, còn refund tự động qua PayOS vẫn ngoài phạm vi.
- Email/SMS marketing và thông báo đại trà; email bảo mật tài khoản được đưa vào phạm vi T33.
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
- Auth đã triển khai theo [ADR 0001](docs/adr/0001-auth-sessions.md) và [ADR 0002](docs/adr/0002-google-authentication.md): session PostgreSQL, cookie HttpOnly, CSRF, register/login/logout/me/password, đăng nhập Google Identity Services (GIS) qua challenge nonce bảo vệ, quản lý liên kết Google, và Admin lock/unlock account có audit.
- React có đăng ký/đăng nhập/đăng xuất/đổi mật khẩu thật, nút Google Sign-In chính thức trên Login & Register, trang `/account/security` quản lý phương thức đăng nhập (Password & Google), header theo session và route guard Customer/Staff/Admin.
- SQL upgrade auth `database/002_auth_sessions.sql` và `database/004_google_auth.sql`, lệnh migrate local và bootstrap Admin đầu tiên (không có default credential).
- Hồ sơ và sổ địa chỉ Customer (T11): GET/PATCH profile, GET/POST/PATCH/DELETE addresses có phân quyền Customer, owner isolation tuyệt đối, transaction khóa row UserAccount để đảm bảo chỉ có tối đa 1 địa chỉ mặc định dưới concurrency, và chặn xóa địa chỉ đã gắn với đơn hàng.
- React có trang `/account` cho khách hàng quản lý hồ sơ và sổ địa chỉ, đồng bộ styling sân cầu lông.
- Product/Media T12, Inventory T13–T14, Cart T15, Checkout T17, COD T18, Fulfillment T25–T26, Notification T27, Reporting T28, deployment hardening T30, Customer Order Center T31, Google Sign-In T32, Password Reset / Email Verification T33, Quản trị tài khoản nội bộ / Staff T34, E2E Testing & Release Gate T35 và Đánh giá & nhận xét sản phẩm T36 đã triển khai. T30 bổ sung liveness/readiness, correlation + JSON logs, persistent Data Protection keys, Docker/Compose và backup/restore runbook. ADR 0003 từng hoãn PayOS để hoàn thiện COD; T19–T20 đã được mở lại có kiểm soát theo ADR 0017 (Proposed), còn feature flag vẫn tắt cho tới khi T21 đạt release gate.
- Kiểm chứng hiện tại (04/11/2026): **226 BE integration tests + 89 FE tests pass** (22 FE test files); AC-20a 100 buyer/10 tồn đã pass; .NET/React build và lint pass (0 warnings, 0 errors). E2E Playwright đạt 100% pass trên Docker demo stack phủ toàn bộ các luồng cốt lõi tích hợp Mailpit API thật. Quản trị nhân sự hỗ trợ mời Staff qua token một lần, Staff tự đặt mật khẩu, gửi lại lời mời, khóa/mở tài khoản, thu hồi toàn bộ session nhân sự nghỉ việc, bảo đảm cấm tạo Admin qua API và cấm chuyển đổi vai trò tùy tiện. Đánh giá sản phẩm bảo đảm Verified Purchase Guard, concurrency partial unique index, che tên reviewer, tính điểm trung bình chính xác từ SQL source-of-truth và kiểm duyệt Staff/Admin đầy đủ Audit Log. Thứ tự khóa User -> Token đồng nhất trên toàn bộ các luồng auth action token loại trừ deadlock PostgreSQL. Hỗ trợ tài khoản Google-only đặt mật khẩu an toàn và chặn Customer chưa verify bypass qua reset password. Seed demo chuẩn hóa idempotent và script 1-click `demo-up.ps1` sẵn sàng. Release gate E2E hoàn toàn xanh.

### Chưa có

- Domain aggregate, state transition guard và business invariant trong project Domain.
- Seed hiện có chỉ phục vụ test và demo (`007_demo_seed.sql`). Seed dữ liệu vận hành production vẫn cần quy trình provisioning riêng.
- Transactional Outbox, broker và notification delivery/push worker cho nghiệp vụ commerce nói chung; notification trong app hiện commit trực tiếp cùng transaction PostgreSQL (riêng auth email đã có transactional outbox T33/T34).
- PayOS sandbox E2E callback thật và provider GET `PAID` fixture có transaction reference; runtime return UI, transition, expiry/cancel race và fulfillment prepaid đã có nhưng feature vẫn mặc định tắt.
- Redis hoặc RabbitMQ.
- Production ingress manifest và hạ tầng HA/managed PostgreSQL cụ thể (CI pipeline đã có qua GitHub Actions `.github/workflows/ci.yml`).

### Ý nghĩa của trạng thái hiện tại

`StoreDbContext` và 32 model chứng minh lớp persistence đã được scaffold, nhưng **không đồng nghĩa nghiệp vụ đã được triển khai**. Nhiều invariant liên bảng không thể chỉ dựa vào model/FK và phải được thực hiện bằng application transaction, guarded SQL và integration test.

`Class1.cs` trong Domain vẫn là placeholder; public catalog chỉ đọc nên chưa tạo domain aggregate. Application và Infrastructure đã có module Catalog.

Tiến độ theo task nằm tại [docs/TASKS.md](docs/TASKS.md). T01–T18, T23–T28, T30–T51 đã hoàn tất; T29 AC-20a pass, AC-20b/c deferred cùng T19–T22 theo ADR 0003. Contract chạy thật: [catalog](docs/api/catalog.md), [catalog search](docs/api/catalog-search.md), [category management](docs/api/categories.md), [brand & specifications](docs/api/brands-product-specifications.md), [product media](docs/api/product-media.md), [product lifecycle](docs/api/product-lifecycle.md), [shipping](docs/api/shipping.md), [reviews](docs/api/reviews.md), [wishlist](docs/api/wishlist.md), [wishlist alerts](docs/api/wishlist-alerts.md), [vouchers](docs/api/vouchers.md), [returns](docs/api/returns.md), [direct exchanges](docs/api/exchanges.md), [auth](docs/api/auth.md), [account](docs/api/account.md), [cart](docs/api/cart.md), [checkout](docs/api/checkout.md), [Customer orders](docs/api/customer-orders.md), [COD confirmation](docs/api/cod-orders.md), [fulfillment](docs/api/fulfillment.md), [notifications](docs/api/notifications.md), [reporting](docs/api/reporting.md), [flash sale](docs/api/flash-sale.md); vận hành: [deployment](docs/operations/deployment.md), [backup/recovery](docs/operations/backup-recovery.md), [release-checklist](docs/operations/release-checklist.md), [e2e-testing-guide](docs/operations/e2e-testing-guide.md), [wishlist-alerts runbook](docs/operations/wishlist-product-alerts.md); quyết định: [ADR 0006](docs/adr/0006-cod-returns-and-manual-refunds.md), [ADR 0007](docs/adr/0007-versioned-database-migration-runner.md), [ADR 0008](docs/adr/0008-returned-goods-inspection-and-quarantine.md), [ADR 0009](docs/adr/0009-direct-product-exchange-and-replacement-shipment.md), [ADR 0011](docs/adr/0011-product-lifecycle-management.md), [ADR 0013](docs/adr/0013-shipping-methods-and-fee-snapshots.md), [ADR 0015](docs/adr/0015-postgresql-native-catalog-search.md), [ADR 0016](docs/adr/0016-wishlist-product-alert-outbox.md).

T40 đã hoàn tất: schema được quản lý bằng CLI one-shot `--migrate-database`, bảng `public.schema_migrations`, SHA-256 checksum, PostgreSQL advisory lock và startup validation fail-fast. Docker Compose chạy migrator trước API cho cả database mới và volume legacy; seed demo 007 tiếp tục tách khỏi lịch sử schema. [Kế hoạch T40](docs/superpowers/plans/2026-11-05-versioned-database-migrations.md).

T41 đã hoàn tất: hàng trả `RECEIVED` đi vào cách ly, Staff phân loại `RESTOCK/QUARANTINE/DAMAGED/SCRAPPED` trước khi chuyển `INSPECTED`, và Admin chỉ hoàn tiền sau kiểm định. Invariant kho là `Physical = Available + Reserved + Allocated + Quarantined + Damaged`. [ADR 0008](docs/adr/0008-returned-goods-inspection-and-quarantine.md).

T42/T42.1 đã hoàn tất: Customer có thể chọn đổi sang SKU khác của cùng Product; Admin giữ tồn thay thế nguyên tử trong 7 ngày; worker nhả tồn hết hạn; hàng cũ vẫn qua kiểm định T41; Staff đóng gói, bàn giao, ghi nhận giao/giao lại và nhận kiện thất bại về quarantine qua luồng replacement shipment riêng. V1 không thu/hoàn chênh lệch giá và không viết ngược Payment/Voucher/Flash Sale. Concurrency/idempotency đã được kiểm chứng trên PostgreSQL thật và Playwright flow 08 chạy trọn vòng đời trên Docker demo stack. [ADR 0009](docs/adr/0009-direct-product-exchange-and-replacement-shipment.md), [API](docs/api/exchanges.md), [kế hoạch hardening](docs/superpowers/plans/2026-11-06-direct-exchange-hardening.md).

T43 đã hoàn tất: Admin quản trị taxonomy danh mục với slug duy nhất, visibility Storefront, Audit Log và guard không xóa danh mục đang có sản phẩm; Staff có thể chọn taxonomy khi tạo/sửa Product Draft. Playwright flow 09 kiểm chứng create → public visibility → hide → delete trên Docker thật. [Kế hoạch](docs/superpowers/plans/2026-11-06-category-management.md), [API](docs/api/categories.md).

T44 đã hoàn tất: Admin quản trị master data thương hiệu; Staff gắn hãng và thông số kỹ thuật có cấu trúc khi sản phẩm còn Draft; Catalog hỗ trợ lọc hãng và PDP hiển thị bảng thông số. Product row lock bảo vệ invariant với Publish. [Kế hoạch](docs/superpowers/plans/2026-11-06-brand-and-product-specifications.md), [API](docs/api/brands-product-specifications.md).

T45 đã hoàn tất triển khai: Staff upload ảnh sản phẩm thật qua multipart, server xác minh chữ ký file và lưu bằng key UUID bất biến trên Docker volume; gallery hỗ trợ alt text, thứ tự và xóa an toàn; public media endpoint tối ưu cache. Storage được tách qua `IProductMediaStorage` để có thể chuyển sang S3/R2. [Kế hoạch](docs/superpowers/plans/2026-11-06-product-media-upload.md), [API](docs/api/product-media.md).

T47/T47.1 đã hoàn tất Product Revision Workflow và hardening: Product Published/Suspended có snapshot riêng để Staff soạn mà không đổi Storefront; Admin review diff content/gallery/specifications theo ba tab rồi approve nguyên tử bằng `content_version`. Migration 017 bổ sung durable media cleanup queue có short lease/retry; bốn checkpoint fault-injection chứng minh rollback toàn transaction. SKU, giá và tồn kho không thuộc revision. Release gate đạt **326/326 backend**, **123/123 frontend**, lint/build sạch và Playwright Flow 13 pass trên Docker demo thật. [Kế hoạch gốc](docs/superpowers/plans/2026-11-06-product-revision-workflow.md), [kế hoạch hardening](docs/superpowers/plans/2026-11-06-product-revision-hardening.md), [ADR 0010](docs/adr/0010-product-revision-workflow.md), [ADR 0012](docs/adr/0012-product-revision-review-and-media-cleanup.md), [API](docs/api/product-revisions.md).

T48 đã hoàn tất Shipping Methods & Fees: Admin quản trị phương thức, mặc định, ETA, ngưỡng miễn phí và phụ phí tỉnh theo optimistic revision; Customer nhận quote từ dữ liệu server và checkout-v3 revalidate nguyên tử trước khi giữ tồn. Order/Payment/COD/Fulfillment lưu snapshot phí và phương thức, đơn lịch sử không đổi khi Admin sửa bảng giá, return không tự hoàn phí ship và reporting tách doanh thu vận chuyển. Demo seed bảo đảm luôn có đúng một active default trên database sạch mà không ghi đè cấu hình hiện hữu. Release gate đạt **357/357 backend**, **130/130 frontend**, lint/build/Compose sạch; Playwright Flow 14 cùng regression Flow 3, 6, 7, 11–13 pass trên Docker demo thật. [Kế hoạch](docs/superpowers/plans/2026-11-06-shipping-methods-fees.md), [ADR 0013](docs/adr/0013-shipping-methods-and-fee-snapshots.md), [API](docs/api/shipping.md).

T49 đã hoàn tất Carrier Consignments & Internal Tracking: Admin quản trị carrier theo optimistic revision; Staff tạo vận đơn cho sales/exchange shipment, tải nhãn PDF/PNG/JPEG có magic-byte validation, bàn giao nguyên tử với stock-out/DeliveryAttempt và ghi tracking event idempotent; Customer xem tracking number, external HTTPS link đã kiểm soát và timeline owner-only. Carrier event không thể bypass fulfillment hoặc tự thu COD. Release gate đạt **375/375 backend**, **134/134 frontend**, lint/build/Compose sạch; Playwright Flow 15 và regression Flow 3, 7, 8, 14 pass trên Docker demo thật. [Kế hoạch](docs/superpowers/plans/2026-11-07-carrier-consignments-tracking.md), [ADR 0014](docs/adr/0014-carrier-consignments-and-tracking-boundary.md), [API](docs/api/carrier-tracking.md).

T50.1 đã hoàn tất nền tảng Advanced Product Search: ADR 0015 chốt PostgreSQL-native search; migration `020` cài `unaccent`/`pg_trgm`, normalize tiếng Việt bằng generated stored columns, partial GIN trigram indexes và supporting indexes cho price/popularity. EF Core mapping không ghi trực tiếp các computed columns. Migration fresh/legacy/reapply/concurrent và toàn bộ **376/376 backend integration tests** đều pass. [Kế hoạch](docs/superpowers/plans/2026-11-07-advanced-product-search-discovery.md), [ADR 0015](docs/adr/0015-postgresql-native-catalog-search.md).

T50.2 đã hoàn tất contract và query normalization: catalog hỗ trợ `inStock`, `minRating`, six-sort allow-list, page size tối đa 48, normalize tiếng Việt không dấu, effective sort động và response query metadata. DTO facets/suggestions đã được chốt nhưng suggestions endpoint chỉ public ở T50.5. `best-selling` đọc source-of-truth 90 ngày, không thêm counter vào fulfillment. Regression đạt **391/391 backend** và **9/9 frontend catalog tests**. [API](docs/api/catalog-search.md).

T50.3 đã hoàn tất product search relevance đa trường: title, SKU/option active, brand active và category visible dùng normalized generated columns + trigram similarity; exact/prefix được ưu tiên trước fuzzy. Lifecycle leakage, inactive dimension, multi-SKU dedupe và deterministic paging đều có integration coverage. Regression đạt **399/399 backend**, frontend catalog giữ **9/9**. [API](docs/api/catalog-search.md).

T50.4 đã hoàn tất facets và advanced sort/filter: category/brand self-exclusion; active-SKU price range; available-stock count; public rating filter/sort; bestseller từ recent completed+delivered source-of-truth. Items/count/facets dùng chung predicate builder. Regression đạt **404/404 backend**, frontend catalog giữ **9/9**. [API](docs/api/catalog-search.md).

T50.5 đã hoàn tất public autocomplete API: Product/SKU/Brand/Category suggestions được normalize/rank/dedupe, bảo vệ lifecycle, truyền cancellation, cache 30 giây và rate-limit riêng 60/phút/IP. Acceptance kiểm chứng request thứ 61 trả 429. Regression đạt **414/414 backend**, catalog suite **63/63**, frontend catalog giữ **9/9**. [API](docs/api/catalog-search.md).

T50.6 đã hoàn tất shared React search UX: Header và Storefront dùng chung `CatalogSearchBox` với debounce 250 ms, abort + stale-response guard, combobox/listbox ARIA, điều khiển bàn phím, click-outside, loading/empty/error-retry và local history tối đa 5 từ khóa có dedupe không dấu. Submit dùng canonical URL helper, reset page và giữ filter khi tìm trong Storefront; Product/SKU suggestion mở thẳng PDP. Regression frontend đạt **141/141**, catalog/search UX **16/16**, lint 0 cảnh báo và production build sạch. [Kế hoạch](docs/superpowers/plans/2026-11-07-advanced-product-search-discovery.md).

T50.7 đã hoàn tất responsive filter workspace: React parse nghiêm ngặt query metadata/facets, hiển thị category/brand count, stock availability, price range, minimum rating và đủ relevance/newest/price/rating/best-selling sort. Active chips, category shortcuts, pagination và form dùng chung canonical URL helper; back/forward phục hồi trạng thái. Mobile drawer có backdrop, focus trap, Escape, sticky actions và cleanup body scroll lock. Regression frontend đạt **145/145**, catalog/search UX **20/20**, lint/build sạch. [Kế hoạch](docs/superpowers/plans/2026-11-07-advanced-product-search-discovery.md).

T50.8 đã hoàn tất performance hardening: tool benchmark tạo database loopback disposable với **10.000 Product/30.000 SKU**, chạy `VACUUM ANALYZE`, bảy `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` và 25 mẫu/case. Năm case search/suggestion bắt buộc dùng GIN trigram, không seq scan search tables; lần đo cuối có suggestion p95 **82,71 ms**, fuzzy p95 **72,77 ms**. Public search/suggestion có command timeout **2 giây** được integration-test và phục hồi setting sau request. Backend đạt **415/415**. [Benchmark](docs/performance/catalog-search-benchmark-latest.md), [kế hoạch](docs/superpowers/plans/2026-11-07-advanced-product-search-discovery.md).

T50.9 đã đóng release gate Advanced Search: demo seed lũy đẳng có Brand, review 5 sao và completed+delivered history đại diện; Playwright Flow 16 kiểm tra search không dấu, exact SKU keyboard autocomplete, combined facets, canonical URL/back-forward, mobile drawer focus/Escape và lifecycle visibility khi suspend/resume. Flow 16 cùng regression Flow 4, 5, 11–14 đều xanh trên Docker thật. CI chạy thêm query-plan benchmark và tự đối chiếu số migration thay vì số ghi cứng. Toàn bộ T50 đã hoàn tất với **415/415 backend**, **145/145 frontend** và **18 Playwright tests/16 specs**.

T51 đã được lập kế hoạch: Customer có thể đăng ký cảnh báo `BACK_IN_STOCK` và `PRICE_DROP` cho sản phẩm đang lưu trong Wishlist. Thiết kế dùng PostgreSQL transactional dirty queue để không bỏ sót thay đổi kho/giá/lifecycle; evaluator tạo notification in-app và commerce email outbox idempotent, còn SMTP chạy ngoài transaction với lease/retry. Giá cảnh báo chỉ là regular price, không trộn Flash Sale. [Kế hoạch T51](docs/superpowers/plans/2026-11-07-wishlist-product-alerts.md).

T51.1 đã hoàn tất: ADR 0016 và migration `021_wishlist_product_alerts.sql` bổ sung alert state cho Wishlist, Product dirty queue có reason mask/event version, commerce email outbox và internal notification action URL. Trigger transactionally bắt thay đổi tồn khả dụng, regular price, SKU status và Product lifecycle; rollback không để lại wake-up. EF/migrator/fixture đã đồng bộ; migration tests **10/10** và toàn bộ backend **416/416** pass. [ADR 0016](docs/adr/0016-wishlist-product-alert-outbox.md).

T51.2 đã hoàn tất owner-only Alert Settings API: `GET/PUT /api/v1/wishlist/items/{productId}/alerts` có CSRF/no-store, verified-email và Product lifecycle guard, snapshot tồn/regular price khi bật, replay cùng payload không tăng revision và stale write trả `409` kèm `currentRevision`. Wishlist list/summary trả alert summary để UI tương lai không N+1. Release gate đạt **421/421 backend**, **145/145 frontend**, lint/build sạch. [API contract](docs/api/wishlist-alerts.md).

T51.3 đã hoàn tất `ProductAlertEvaluationWorker`: claim queue bằng short lease/`SKIP LOCKED`, xử lý subscriber theo batch, phát hiện có hàng lại và regular-price giảm, ghi observed state + in-app notification + commerce outbox trong cùng transaction. Queue chỉ bị xóa khi `event_version` không đổi; mutation đến giữa evaluation được giữ lại và lease giải phóng. Hai worker claim rời nhau, lease hết hạn phục hồi, lỗi có backoff/sanitized code; backend đạt **426/426**.

T51.4 đã hoàn tất kênh commerce email: `CommerceEmailDeliveryWorker` claim batch bằng transaction ngắn, gửi SMTP hoàn toàn ngoài transaction với timeout 30 giây từng email, persist outcome và giải phóng lease ngay từng item. Worker phục hồi lease cũ, retry/backoff tối đa 5 lần, revalidate account/email snapshot cùng payload/internal action URL, chỉ hỗ trợ template whitelist và che PII trong log. Hai worker không gửi trùng batch; test SMTP chậm chứng minh item trước được commit ngay. Backend đạt **431/431**. Bước tiếp theo là T51.5 UI Wishlist/PDP/Notifications.

T51.5 đã hoàn tất UI cảnh báo Wishlist/PDP/Notifications: summary state dùng chung tránh N+1, dialog responsive có fieldset/legend, focus trap, Escape/return-focus, pending và stale revision reload; session trả trạng thái email verified để chặn kênh email sớm. Product unavailable chỉ cho tắt alert. Notification feed trả `actionUrl`, phân loại alert có hàng/giảm giá và frontend loại bỏ URL absolute/protocol-relative trước khi render CTA. Release đạt **431/431 backend**, **149/149 frontend**, lint và production build sạch.

T51.6 đã hoàn tất hardening/fault-injection: kiểm chứng concurrent remove/update, xóa Wishlist sau worker snapshot, multi-SKU coalesce, reservation/release tồn, chuỗi new-low price, suspend/resume, Flash Sale isolation và disable/re-enable baseline. Evaluator và commerce email worker cố giải phóng lease trong tối đa 5 giây khi cancellation; lease expiry vẫn là fallback và retry không tạo business event lặp. Frontend có thêm test email chưa verified và account-switch stale response. Release đạt **441/441 backend**, **151/151 frontend**, lint/build sạch.

T51.7 đã hoàn tất Playwright Flow 17 trên Docker/PostgreSQL/Mailpit thật: tạo sản phẩm hết hàng cô lập, bật cả cảnh báo có hàng và giảm regular price qua UI, stock-in/price update phát đúng một in-app notification và một email, reload không nhân bản, sau opt-out không phát thêm. Regression Flow 1, 5, 8, 11, 12 và 16 xanh; Flow 8 được harden dùng Product demo hai SKU cố định thay vì phụ thuộc thứ tự Catalog. Playwright hiện nhận diện **19 tests/17 specs**.

T51.8 đã hoàn tất operations và release gate: có runbook theo dõi queue/outbox backlog, lease, retry/error, ngưỡng cảnh báo, triage và recovery an toàn; CI xác minh Flow 17 được discover trước khi chạy toàn bộ Flows 1–17. Tài liệu API/deployment/E2E/release checklist đã đồng bộ. Nghiệm thu cuối: **441/441 backend**, **151/151 frontend**, **19 tests/17 Playwright specs**, lint/build và Compose validation sạch.

T52 đã được chốt kế hoạch: Guest và người dùng đăng nhập có thể chọn tối đa bốn Product cùng danh mục từ Catalog/PDP, chia sẻ URL `/compare?ids=...` và xem matrix regular price, availability, rating, SKU cùng thông số kỹ thuật. Thiết kế dùng batch Catalog API public không N+1, không migration/database account state; localStorage chỉ giữ lựa chọn tiện dụng và URL là source of truth. [Kế hoạch T52](docs/superpowers/plans/2026-11-08-product-comparison.md).

T52.1 đã hoàn tất contract foundation: query giới hạn 1–4 UUID duy nhất/khác empty, response giữ thứ tự cùng requested/omitted count và mã lỗi category mismatch ổn định. Các contract này là nền tảng cho batch endpoint đã được mở ở T52.2. [API](docs/api/product-comparison.md).

T52.2 đã hoàn tất backend batch API: anonymous `GET /api/v1/catalog/products/compare` giữ thứ tự request, chỉ chiếu dữ liệu public, omit lifecycle/unavailable an toàn, kiểm tra cùng underlying category kể cả taxonomy ẩn và chạy đúng một database command. Category mismatch dùng ProblemDetails ổn định, response `no-store`. Release backend đạt **455/455**, riêng T52 **14/14**. [API](docs/api/product-comparison.md).

T52.3 đã hoàn tất demo comparison data: hai vợt demo cùng danh mục có chung 6 `spec_key` và 12 dòng thông số, gồm cả giá trị giống/khác để kiểm chứng matrix. Upsert chỉ tác động demo IDs, chỉ ghi khi nội dung đổi và chạy seed lặp không duplicate; batch API trả đủ sáu thông số mỗi Product. **15/15 seed + comparison tests** pass.

T52.4 đã hoàn tất frontend comparison state: parser localStorage kiểm tra UUID/dedupe/cap 4, tự phục hồi dữ liệu hỏng và giữ memory state khi storage bị chặn; Context account-agnostic đồng bộ tab, chặn khác danh mục/slot thứ năm. Catalog/PDP có CompareButton accessible và tray responsive hỗ trợ remove/clear/live notice. CTA tray chưa điều hướng cho đến khi trang `/compare` hoàn tất ở T52.5. Release frontend đạt **156/156**, lint/build sạch.

T52.5 đã hoàn tất trang `/compare`: loader canonicalize/dedupe/cap URL và gọi đúng batch API, parser kiểm tra order/count, sau đó đồng bộ storage theo Product còn public. Trang có empty/one/unavailable states, bảng semantic cuộn ngang, core rows và union specifications, ký hiệu thiếu dữ liệu, highlight khác biệt và toggle `different=true`; remove cập nhật URL, CTA tray đã hoạt động. Release frontend đạt **160/160**, lint/build sạch.

T52.6 đã hoàn tất hardening: batch comparison có database command timeout 2 giây và hoàn nguyên cấu hình, client parser từ chối rating/count/order/category response sai, metadata ảnh localStorage được chặn scheme nguy hiểm. Thao tác remove giữ `different=true` và chuyển focus bàn phím sang mục kế; lỗi khác danh mục có hướng dẫn riêng. Release gate Testcontainers được ổn định bằng xUnit conservative tối đa bốn collection threads. Nghiệm thu đạt **455/455 backend**, **162/162 frontend**, lint/build và base+demo Compose sạch.

T52.7 đã hoàn tất Flow 18 trên Docker/PostgreSQL thật: Guest chọn hai vợt, reload/share URL sang context mới, kiểm tra matrix và different-only, remove-add không duplicate, chặn giày khác danh mục; Admin suspend khiến Product bị omit bằng thông báo generic rồi luôn resume trong `finally`. E2E đã phát hiện và sửa GUID parser frontend từng ép RFC version nibble không tương thích demo GUID của .NET/PostgreSQL. Regression Flows 5/12/16/17 đạt 4/4; CI hiện discover Flow 18 và chạy Flows 1–18. Playwright có **20 tests/18 specs**.

T53 đã hoàn tất và qua audit hardening: “Sản phẩm tương tự & Đã xem gần đây” có public related API (ranking deterministic in-stock → same active brand → price distance → rating → review count → ID; tối đa 2 DB commands, timeout 2s) và recent history client-only trong `localStorage` (tối đa 12, TTL 30 ngày, dedupe, loại timestamp tương lai quá 5 phút) được hydrate qua batch API (đúng 1 DB command, giữ order, prune omitted ID). Audit đã bổ sung generic 404 cho `Guid.Empty`, parser frontend strict, image URL sanitation, đồng bộ xóa nhiều tab và bảo toàn PDP hiện tại khi prune. Không AI/ML, không server-side tracking, không account sync và không migration. T53 backend đạt **24/24**, toàn bộ frontend đạt **178/178**; Playwright Flow 19 cùng regressions 5/12/16/17/18 đạt **6/6 trên Docker thật**. [Kế hoạch T53](docs/superpowers/plans/2026-11-08-related-products-recently-viewed.md), [API](docs/api/product-discovery.md).

T46 đã hoàn tất: Admin có thể tạm ngừng, mở bán lại và lưu trữ terminal Product; Catalog/Cart/Checkout/Wishlist/Flash Sale và T47 dùng chung lifecycle contract. Product row lock tuyến tính hóa Suspend với Checkout, giữ nguyên dữ liệu lịch sử và mọi counter khi request bị chặn. Release gate đạt **316/316 backend**, **122/122 frontend**, migration 016 và Playwright flow 12 pass trên Docker thật. [Kế hoạch](docs/superpowers/plans/2026-11-06-product-lifecycle-management.md), [ADR 0011](docs/adr/0011-product-lifecycle-management.md), [API](docs/api/product-lifecycle.md).

Release gate hiện tại: **480/480 backend integration tests** (T53 **24/24**), **178/178 frontend tests**, oxlint sạch (0 lỗi, 0 cảnh báo), production build và base+demo Docker Compose validation thành công. Audit concurrency bổ sung bài test cạnh tranh Review ↔ Full Refund có điều phối khóa thật; khóa Customer của Review dùng `FOR NO KEY UPDATE` để vẫn tuần tự hóa tạo review nhưng không deadlock với khóa FK `KEY SHARE` từ luồng hoàn tiền. Playwright nhận diện **21 tests/19 specs**; Flow 19 và regressions bắt buộc 5/12/16/17/18 đã pass **6/6** trên Docker demo thật.

T39/T41 đã hoàn tất: đổi/trả và hoàn tiền thủ công cho đơn COD, với hàng nhận lại đi vào quarantine exactly-once, kiểm định đủ bốn disposition trước khi hoàn tiền, phân bổ tiền hoàn trên giá thực trả sau Flash Sale/Voucher, lock order thống nhất và audit đầy đủ. Hiện có **290 backend integration tests**, **111 frontend tests** và Playwright E2E flow 07 chạy thật qua Docker. [Kế hoạch T39](docs/superpowers/plans/2026-11-05-cod-returns-manual-refunds.md), [T41](docs/superpowers/plans/2026-11-05-return-quality-inspection.md).

T38 đã hoàn tất: mã giảm giá cơ bản (Fixed Amount & Percentage), áp dụng trên subtotal sau Flash Sale, kiểm tra hạn mức tổng lượt / mỗi khách / ngân sách tài trợ, phân bổ HELD nguyên tử khi checkout, giải phóng RELEASED khi hủy đơn / hết hạn COD / giao thất bại hoàn kho, tiêu thụ CONSUMED khi thu tiền COD thành công, snapshot subtotal và voucher discount trên đơn hàng, chống deadlock qua thứ tự khóa chuẩn hóa, bảo toàn tương thích Idempotency (checkout-v1 / checkout-v2), giao diện giỏ hàng CartPage, không gian quản trị Admin Dashboard và kịch bản Playwright E2E thật 100% pass trên Docker demo stack. [Kế hoạch T38](docs/superpowers/plans/2026-11-04-basic-vouchers.md).

T37 đã hoàn tất: wishlist Customer lưu theo Product (tối đa 500 sản phẩm), shared state cô lập theo tài khoản và chống stale response, Guest giữ `returnTo`, nhiều SKU bắt buộc chọn rõ ràng trong dialog hỗ trợ bàn phím, thêm giỏ không tự xóa yêu thích, owner isolation, mutation PUT/DELETE idempotent (204 No Content), phân loại trạng thái tồn kho (`AVAILABLE`, `OUT_OF_STOCK`, `PRODUCT_UNAVAILABLE`), placeholder cho sản phẩm ngừng bán, live count badge Header, và bộ tests/E2E toàn diện. [Kế hoạch T37](docs/superpowers/plans/2026-11-04-wishlist.md).

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

Lệnh migrate chỉ chấp nhận PostgreSQL loopback, áp upgrade 002 và 004 (`database/004_google_auth.sql`) có transaction, không tạo tài khoản. Workspace local đã áp các bản migration này. API khởi động bình thường không tự migrate.

Để bật đăng nhập Google, cấu hình trong `appsettings.json` hoặc biến môi trường:
```json
"GoogleAuth": {
  "Enabled": true,
  "ClientId": "<your-google-oauth-client-id>.apps.googleusercontent.com"
}
```
Hoặc qua biến môi trường: `GoogleAuth__Enabled=true` và `GoogleAuth__ClientId=...`. Client Id là public; không cần và không lưu Client Secret trong flow GIS trình duyệt.

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

Đã kiểm chứng create/query/cancel và chữ ký request trên kênh test ngày 09/11/2026. Còn phải kiểm chứng trên môi trường PayOS:

- Cách đặt `expiredAt` chính xác.
- Webhook signature payload thực tế cho giao dịch thanh toán thành công.
- Mapping nhiều attempts.
- Webhook acknowledgment/retry.
- Tra cứu transaction tại deadline.
- Trường hợp thu dư hoặc callback đến muộn.

### OPEN-AUTH-01

Auth đã chốt tại ADR 0001: cookie HttpOnly, PostgreSQL session 7 ngày, revoke và CSRF; me/logout mở cho authenticated. Các quyền `[DX]` khác chưa chốt, không tự mở.

### OPEN-DB-01

Schema được quản lý bằng numbered SQL embedded và `public.schema_migrations`; migrator one-shot chạy trước API, kiểm tra checksum, advisory lock và binary downgrade. Mọi schema change mới phải thêm script số mới, manifest và fixture tương ứng.

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

1. PayOS adapter sau khi VERIFY provider contract — backend foundation hoàn tất.
2. Payment attempts — backend foundation hoàn tất.
3. Webhook signature/receipt — durable inbox hoàn tất; transaction áp dụng payment chuyển sang T21.
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
