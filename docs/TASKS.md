# Tiến độ HighConcurrencyCommerce

Ngày bắt đầu: 30/10/2026. Đọc cùng `../README.md`. Mỗi task kết thúc bằng hành vi chạy được và bằng chứng kiểm thử; không coi scaffold là feature hoàn chỉnh.

## Đợt 1 — Catalog công khai xuyên suốt BE → FE (hoàn tất 30/10/2026)

| ID | Task nhỏ | Điều kiện hoàn thành | Trạng thái |
|---|---|---|---|
| T01 | Đưa schema nguồn vào repo, dựng integration test PostgreSQL riêng | Test tạo DB tạm, áp dụng schema thật, tự dọn DB của chính test | Xong |
| T02 | API categories công khai | Chỉ trả category visible, DTO không lộ entity nội bộ | Xong |
| T03 | API products tìm kiếm/lọc/phân trang | Chỉ PUBLISHED có ACTIVE SKU, query được validate, thứ tự ổn định | Xong |
| T04 | API product detail | 404 với Draft/Archived, chỉ trả ACTIVE SKU, tồn dựa available; ảnh theo T12 | Xong |
| T05 | React catalog và trang chi tiết dùng API | Search/filter/paging, URL lưu bộ lọc, loading/empty/error/retry | Xong |
| T06 | Test FE + kiểm chứng FE/BE + hướng dẫn chạy | Test hành vi, build, lint; cập nhật README và API contract | Xong |

Bằng chứng: 25 integration tests PostgreSQL pass; 12 FE tests pass; solution build 0 warning/error; React production build và lint pass. Smoke test HTTP trực tiếp API và qua Vite proxy đều 200. Database ứng dụng hiện trả 0 sản phẩm; không tự seed dữ liệu. Test có fixture riêng để kiểm tra đủ tình huống có dữ liệu. Chưa có browser E2E/visual review và load test.

Contract: [api/catalog.md](api/catalog.md).

## Đợt 2 — Auth/session (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T07 | Chốt cơ chế auth | Xong — chủ dự án duyệt cookie HttpOnly + PostgreSQL session; ADR 0001, DX ngoài me/logout chưa mở |
| T08 | Đăng ký Customer | Xong — normalize email, unique race guard, hash password, không nhận role từ client, response chung |
| T09 | Session và quyền backend | Xong — login/logout/me/password, CSRF, expiry/revoke, Admin status + audit, role policies, rate limits và bootstrap Admin |
| T10 | React auth/guard | Xong — register/login/logout/password thật, header session, guards và returnTo chỉ trong origin |

Bằng chứng khi kết thúc đợt 2: **45 BE integration tests pass** (25 catalog + 20 auth/bootstrap), **22 FE tests pass**; .NET build 0 warning/error, React build và lint pass. Có test đăng ký/Bootstrap chạy đồng thời, cookie flags, CSRF cross-client, expiry tuyệt đối, logout riêng thiết bị, đổi password/role/status hủy phiên, Admin audit và rate limits.

Schema auth 002 đã áp vào PostgreSQL local; không tạo account giả/Admin mặc định. Smoke qua Vite đang chạy: `/auth/csrf` 200, guest `/auth/me` 401, catalog 200, `/login` 200 (HTTP shell; không phải browser E2E). Tài liệu: [ADR](adr/0001-auth-sessions.md), [auth contract và lệnh bootstrap](api/auth.md).

## Đợt 3 — Hồ sơ và sổ địa chỉ Customer (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T11 | Hồ sơ/địa chỉ Customer | Xong — Owner-only tuyệt đối, cập nhật profile (fullName, phone), CRUD địa chỉ, khóa row UserAccount bảo vệ 1 địa chỉ default duy nhất dưới concurrency, chặn xóa địa chỉ đã gắn với đơn hàng (409 ADDRESS_IN_USE), React UI quản lý hồ sơ & sổ địa chỉ tại `/account`. |

Bằng chứng đợt 3: **57 BE integration tests pass** (25 catalog + 20 auth + 6 bootstrap + 6 account/address concurrency), **29 FE tests pass**; .NET build 0 warning/error, React build và lint pass. Test bao gồm race condition nhiều request cùng set default address, owner isolation cross-customer, header Location đúng chuẩn `/account/addresses/{id}`, kiểm tra responsive layout, và foreign key restriction khi xóa địa chỉ có order. Contract: [api/account.md](api/account.md).

## Đợt 4 — Quản trị sản phẩm Draft/SKU/Giá/Publish và Media ảnh (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T12 | Product Draft/SKU/price/Publish và media | Xong — Staff soạn Draft/thêm SKU/thêm xóa ảnh, Admin định giá SKU/Publish kiểm duyệt, bắt buộc ghi audit log bất biến, ràng buộc toàn vẹn khi publish (DRAFT + có SKU active + giá > 0), Public Catalog API trả về ảnh active đầu tiên (imageUrl) & danh sách ảnh (images), UI Staff (/staff) & Admin (/admin) & Catalog gallery đồng bộ. |

Bằng chứng đợt 4: **67 BE integration tests pass** (25 catalog + 20 auth + 6 bootstrap + 6 account + 10 product management RBAC/lifecycle/concurrency/validation), **32 FE tests pass** (29 cũ + 1 catalog image gallery + 2 staff/admin dashboard); .NET build 0 warning/error, React build và lint pass. Kiểm tra tự động bao phủ:
1. RBAC: Guest (401), Customer (403), Staff bị cấm sửa giá & publish (403), Admin bị cấm can thiệp staff draft (403).
2. Quy trình trọn vẹn: Tạo draft → Sửa draft → Thêm SKU → Chặn trùng SKU (409) → Thêm ảnh media → Xóa ảnh → Cố publish khi chưa có giá (400 CANNOT_PUBLISH) → Admin cập nhật giá SKU & ghi audit log (`SKU_PRICE_UPDATED`) → Admin Publish sản phẩm & ghi audit log (`PRODUCT_PUBLISHED`) → Chặn Staff sửa draft/thêm ảnh/xóa ảnh sau khi đã publish (400 INVALID_STATE).
3. Concurrency & Race condition toàn diện:
   - Khóa hàng `store.products FOR UPDATE` trên **toàn bộ mutation của Staff** (`UpdateDraftAsync`, `AddSkuAsync`, `AddImageAsync`, `DeleteImageAsync`) và `PublishProductAsync` của Admin:
     - Chặn đứng 100% kịch bản Staff mutation chạy xuyên qua Publish.
     - Kiểm chứng race độc lập giữa Publish và toàn bộ các mutation của Staff:
       - Race Publish vs Staff Add SKU giá 0: hoặc Staff thêm xong và Publish bị từ chối (400 `CANNOT_PUBLISH`), hoặc Publish xong và Staff bị từ chối (400 `INVALID_STATE`); database bảo đảm sản phẩm `PUBLISHED` không bao giờ tồn tại SKU active giá <= 0.
       - Race Publish vs Staff Update Draft: hoặc Staff xong trước và tiêu đề mới được publish, hoặc Publish xong và Staff nhận 400 `INVALID_STATE`.
       - Race Publish vs Staff Add Image: hoặc ảnh thêm trước khi publish, hoặc Publish xong và Staff nhận 400 `INVALID_STATE`.
       - Race Publish vs Staff Delete Image: hoặc ảnh bị xóa trước khi publish, hoặc Publish xong và Staff nhận 400 `INVALID_STATE`.
     - Kiểm chứng race giữa Publish song song: đúng 1 request thành công, request thứ hai nhận 400 `CANNOT_PUBLISH`, chỉ có duy nhất 1 bản ghi `PRODUCT_PUBLISHED` trong `store.audit_logs`.
   - Khóa hàng `store.product_skus FOR UPDATE` tuần tự hóa thao tác cập nhật giá SKU.
   - Bắt `DbUpdateException` (Unique Violation 23505) chuyển thành 409 `CONFLICT` khi tạo SKU đồng thời, bảo vệ hệ thống tuyệt đối không trả 500.
4. Public Catalog & Media ordering: Sản phẩm Draft bị ẩn hoàn toàn (404); sau khi Publish xuất hiện tức thì trên Catalog list và Detail kèm ảnh active thật. Các ảnh có cùng `display_order` được sắp xếp theo `created_at` tăng dần.
5. Frontend UI: Giao diện Staff (`/staff`) và Admin (`/admin`) hoạt động ổn định; Admin UI chỉ yêu cầu các biến thể SKU `ACTIVE` phải có giá bán $> 0$ trước khi Publish (cho phép SKU `INACTIVE` giá 0).
6. Tài liệu contract: [api/product-management.md](api/product-management.md), Kế hoạch thực hiện: [docs/superpowers/plans/2026-10-30-product-management-media.md](superpowers/plans/2026-10-30-product-management-media.md).

## Đợt 5 — Kho: xem tồn kho, nhập tăng tồn kho và sổ cái dịch chuyển (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T13 | Kho: xem tồn, nhập tăng, lịch sử | Xong — Staff xem danh sách tồn kho theo SKU (`GET /api/v1/staff/inventory`), nhập tăng tồn kho trực tiếp (`POST /api/v1/staff/inventory/stock-in`), xem sổ cái dịch chuyển movements (`GET /api/v1/staff/inventory/{skuId}/movements`), bảo toàn đẳng thức phân vùng $PhysicalQty = AvailableQty + ReservedQty + AllocatedQty$, tuần tự hóa khóa hàng cha `ProductSku FOR UPDATE` chống lost update & race duplicate insert, idempotent replay theo `(product_sku_id, movement_type, source_type, source_id)`. Giao diện Staff Dashboard tích hợp tab Kho & Tồn kho đầy đủ. |

Bằng chứng đợt 5: **73 BE integration tests pass** (25 catalog + 20 auth + 6 bootstrap + 6 account + 10 product management + 6 inventory), **33 FE tests pass** (32 cũ + 1 inventory tab/stock-in/movements); .NET build 0 warning/error, React build và lint pass. Kiểm tra tự động bao phủ:
1. RBAC: Guest nhận 401 Unauthorized, Customer và Admin đều nhận 403 Forbidden (nguyên tắc phân quyền nghiêm ngặt: chỉ duy nhất role `STAFF` được phép vận hành kho, Admin và Customer bị cấm tuyệt đối), chỉ Staff nhận 200 OK.
2. Nhập kho & Ledger invariant: Nhập tăng $+X$ làm tăng đồng thời $PhysicalQty \leftarrow PhysicalQty + X$ và $AvailableQty \leftarrow AvailableQty + X$, giữ nguyên $ReservedQty$ và $AllocatedQty$; ghi nhận chính xác 1 bản ghi vào `store.inventory_movements` (`movement_type = 'STOCK_IN'`).
3. Idempotency toàn diện từ UI đến DB:
   - Frontend: Form nhập kho sinh và duy trì `sourceId` (UUID) trong state; nếu submit gặp sự cố mạng hoặc lỗi, thao tác retry vẫn bảo toàn cùng `sourceId` này; chỉ sau khi nhập kho thành công mới tạo `sourceId` mới cho đợt nhập tiếp theo.
   - Backend Replay: Gửi lại cùng `sourceType` và `sourceId` với cùng số lượng/ghi chú sẽ trả về số dư tồn kho hiện tại (200 OK) mà không cộng dồn lần 2.
   - Idempotency Conflict Guard: Gửi lại cùng `sourceId` nhưng thay đổi `Quantity` hoặc `Note` bị từ chối 409 `IDEMPOTENCY_CONFLICT`, loại bỏ triệt để nguy cơ hiểu nhầm payload mới đã được xử lý.
4. Concurrency & Race condition toàn diện:
   - Khóa hàng cha `store.product_skus WHERE id = {skuId} FOR UPDATE` ngay đầu transaction: tuần tự hóa an toàn cả khi SKU chưa có bản ghi ban đầu trong `store.inventory_balances`, loại bỏ triệt để hiện tượng lost update và race duplicate INSERT khi nhiều thread cùng nhập kho cho SKU mới.
   - Chạy đồng thời 8 request nhập kho song song cho cùng một SKU: toàn bộ 8 request đều thành công, $PhysicalQty$ và $AvailableQty$ tăng chính xác bằng tổng số lượng nhập, không bị lost update.
   - Chạy đồng thời 2 request có cùng `sourceId` và `sourceType`: 1 request thắng commit thành công, request thua đụng unique constraint được bắt và xử lý mượt mà (trả 409 `CONFLICT` hoặc replay idempotency, tuyệt đối không trả 500).
5. Validation & Exception mapping: Chặn số lượng $\le 0$ (400 `INVALID_ARGUMENT`), lỗi model validation (400 `VALIDATION_ERROR`), chặn SKU không tồn tại (404 `NOT_FOUND`).
6. Frontend UI: Tab "🏭 Vận hành Kho & Tồn kho" tại `/staff` hiển thị bảng phân vùng tồn kho trực quan, form nhập hàng nhanh có bảo vệ CSRF và Idempotency Key, phản hồi tức thì, panel xem sổ cái biến động (Movements Ledger) cho phép theo dõi chi tiết từng lần nhập xuất.
7. Tài liệu contract: [api/inventory.md](api/inventory.md), Kế hoạch thực hiện: [docs/superpowers/plans/2026-10-30-inventory-stock-in.md](superpowers/plans/2026-10-30-inventory-stock-in.md).

## Đợt 6 — Đề nghị/duyệt giảm tồn (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T14 | Đề nghị/duyệt giảm tồn | Xong — Staff tạo request Pending không đổi tồn; Admin approve/reject một lần; approve khóa request → SKU → balance, recheck available, chỉ giảm Physical/Available, ghi movement ADJUST và Audit atomically; reject không có side effect. UI Staff/Admin hoàn chỉnh. |

Bằng chứng đợt 6: **79 BE integration tests pass** (73 cũ + 6 adjustment RBAC/lifecycle/concurrency), **35 FE tests pass** (33 cũ + 2 Staff/Admin adjustment); .NET build 0 warning/error, React build và lint pass. Contract: [api/inventory-adjustments.md](api/inventory-adjustments.md).

## Đợt 7 — Guest cart và Customer cart/merge (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T15 | Guest cart local và Customer cart/merge | Xong — Guest cart versioned trong localStorage; Customer cart owner-only ở PostgreSQL; add/update/remove/merge/validate; merge sau login; chỉ nhận SKU + quantity và đọc lại giá/trạng thái server; khóa Customer `FOR UPDATE` chống lost update; không mutation hoặc giữ tồn kho. |

Bằng chứng đợt 7: **85 BE integration tests pass** (79 cũ + 6 cart RBAC/ownership/validation/merge/concurrency), **38 FE tests pass** (35 cũ + 3 Guest/Customer/login merge); .NET build 0 warning/error, React build và lint pass. Contract: [api/cart.md](api/cart.md).

## Đợt 8 — Chốt idempotency checkout và terminal Payment COD (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T16 | Chốt OPEN-IDEMP và OPEN-COD | Xong — ADR 0002 chốt canonical payload/hash, replay SUCCEEDED/REJECTED, savepoint rollback business rejection, transient rollback toàn bộ, không commit PROCESSING lửng, lock timeout không takeover, PayOS tách khỏi checkout transaction; COD terminal chưa thu dùng `VOID`. |

T16 là task quyết định kiến trúc, không thêm runtime code/test. Quyết định và test matrix bắt buộc cho T17–T21 nằm tại [ADR 0002](adr/0002-checkout-idempotency-and-cod-void.md).

## Đợt 9 — Checkout transaction nhiều SKU và snapshots (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T17 | Checkout transaction nhiều SKU + snapshots | Xong — explicit lines; canonical idempotency; Customer → SKU → Inventory row locks; snapshot địa chỉ/title/SKU/giá; Order/Payment/Reservation/movement/inventory/cart atomically; savepoint lưu REJECTED nhưng rollback mọi commerce side effect; COD 24h, PayOS 15m không gọi provider; React checkout hoàn chỉnh. |

Bằng chứng đợt 9: **92 BE integration tests pass** (85 cũ + 7 checkout RBAC/snapshot/AON/idempotency/concurrency), **40 FE tests pass** (38 cũ + 2 checkout/idempotent retry); .NET build 0 warning/error, React build và lint pass. Contract: [api/checkout.md](api/checkout.md).

## Đợt 10 — COD limit, Staff confirmation và expiry Worker (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T18 | COD: limit, xác nhận, expiry Worker | Xong — tối đa 2 pending dưới customer lock; Staff queue/confirm chuyển Reserved→Allocated và Order CONFIRMED; Worker 24h chuyển Reserved→Available, Order EXPIRED, Payment VOID, Reservation RELEASED; movement/audit và exactly-once guards nguyên tử; React Staff COD queue. |

Bằng chứng đợt 10: **97 BE integration tests pass** (92 cũ + 5 COD RBAC/limit/confirm/double-confirm/worker-expiry), **41 FE tests pass** (40 cũ + 1 Staff COD queue/confirm); .NET build 0 warning/error, React build và lint pass. Contract: [api/cod-orders.md](api/cod-orders.md).

## Quyết định roadmap — hoãn PayOS (chốt 30/10/2026)

Theo [ADR 0003](adr/0003-defer-payos-complete-cod-first.md), T19–T22 được hoãn cho tới khi có PayOS sandbox credential và bằng chứng contract thực tế. Checkout `PAYOS` hiện trả `409 PAYOS_DEFERRED`; UI không cho chọn. Đây là trì hoãn có chủ đích, không phải triển khai giả provider/webhook.

## Đợt 11 — Fulfillment và thu COD thành công (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T25 | Fulfillment và COD collected | Xong — Staff-only queue; `CONFIRMED → FULFILLING`, Shipment `PENDING_PACKING → PACKED → SHIPPING → DELIVERED`; xuất kho tại handover bằng `Physical/Allocated -= qty`, Reservation/Lines `CONSUMED`, ledger `STOCK_OUT`; giao thành công chỉ commit khi thu đúng tổng COD và mã biên nhận duy nhất, đồng thời tạo PaymentTransaction `VERIFIED`, Payment `COD_COLLECTED`, Order `COMPLETED`. |

Bằng chứng đợt 11: **102 BE integration tests pass** (97 cũ + 5 fulfillment RBAC/lifecycle/rollback/concurrency), **42 FE tests pass** (41 cũ + 1 luồng Staff fulfillment bốn bước); .NET build, React build và lint pass. Test đồng thời hai request giao thành công chứng minh chỉ một request thắng và chỉ một PaymentTransaction được tạo. Contract: [api/fulfillment.md](api/fulfillment.md), kế hoạch/kết quả: [superpowers/plans/2026-10-30-cod-fulfillment.md](superpowers/plans/2026-10-30-cod-fulfillment.md).

## Đợt 12 — Giao lại và hoàn kho exactly-once (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T26 | Retry giao/return stock | Xong — ghi `DELIVERY_FAILED` theo từng attempt; tối đa hai lần giao lại (attempt 2–3); retry và return-stock tranh chấp cùng Order/Shipment lock; hoàn kho tăng Physical/Available, ghi `RETURN_IN`, giữ Reservation `CONSUMED`, đặt cờ exactly-once, Payment COD `VOID`, Order `CANCELLED/DELIVERY_FAILED_RETURNED`. UI Staff hỗ trợ lý do thất bại, số lượt retry và chứng từ hàng về. |

Bằng chứng đợt 12: **105 BE integration tests pass** (102 cũ + 3 retry/return concurrency), **43 FE tests pass** (42 cũ + 1 failed/retry/return UI); .NET build 0 warning/error, React build và lint pass. Contract: [api/fulfillment.md](api/fulfillment.md), kế hoạch/kết quả: [superpowers/plans/2026-10-30-delivery-retry-return-stock.md](superpowers/plans/2026-10-30-delivery-retry-return-stock.md).

## Đợt 13 — In-app notifications recipient-only (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T27 | In-app notification | Xong — feed/read cho mọi role đã đăng nhập nhưng owner-only tuyệt đối; cross-recipient trả 404; mark-read idempotent; event key `(recipient,event)` chống duplicate bằng unique index + `ON CONFLICT DO NOTHING`; notification commit/rollback cùng transaction COD/fulfillment. React `/notifications` hiển thị unread count và đánh dấu đã đọc. Không thêm Outbox/broker khi chưa có ADR. |

Bằng chứng đợt 13: **106 BE integration tests pass** (105 cũ + 1 notification auth/ownership/read, đồng thời mở rộng race delivery để kiểm tra notification duy nhất), **44 FE tests pass** (43 cũ + 1 notification feed/read); .NET build 0 warning/error, React build và lint pass. Contract: [api/notifications.md](api/notifications.md), kế hoạch/kết quả: [superpowers/plans/2026-10-30-in-app-notifications.md](superpowers/plans/2026-10-30-in-app-notifications.md).

## Đợt 14 — Reporting, Staff tasks và Audit query (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T28 | Reports, Staff tasks và audit query | Xong — Admin-only revenue/low-stock/audit, Staff-only task summary; revenue chỉ tính Payment của Order COMPLETED có transaction VERIFIED khớp expected amount; low-stock là Available của ACTIVE SKU/PUBLISHED Product dưới ngưỡng cấu hình mặc định 5; audit filter/page/date guard. UI Staff/Admin hoàn chỉnh. |

Bằng chứng đợt 14: **109 BE integration tests pass** (106 cũ + 3 reporting/RBAC/query), **46 FE tests pass** (44 cũ + 2 Staff/Admin reporting); .NET build 0 warning/error, React build và lint pass. Contract: [api/reporting.md](api/reporting.md), kế hoạch/kết quả: [superpowers/plans/2026-10-30-reporting-staff-tasks-audit.md](superpowers/plans/2026-10-30-reporting-staff-tasks-audit.md).

## Đợt 15 — High-concurrency acceptance (cập nhật 30/10/2026)

| ID | Case | Kết quả |
|---|---|---|
| T29 / AC-20a | 100 Customer mua đồng thời SKU còn 10 | **Pass** — start gate nhả cùng lúc 100 POST checkout thật; đúng 10 Created, 90 `OUT_OF_STOCK`; inventory `10=0+10+0`; đúng 10 OrderItem/movement, không oversell. |
| T29 / AC-20b | 5 callback PayOS trùng | **Deferred — ADR 0003**; cần sandbox signature/webhook/attempt thật, không mock provider contract. |
| T29 / AC-20c | Cancel/expiry cạnh tranh payment | **Deferred — ADR 0003**; cần verified payment, reconciliation và cancellation PayOS. |

Bằng chứng hiện tại: **110 BE integration tests pass** (109 cũ + AC-20a); FE không đổi ở **46 tests pass**, lint/build pass. Ma trận và điều kiện mở lại: [superpowers/plans/2026-10-30-concurrency-acceptance.md](superpowers/plans/2026-10-30-concurrency-acceptance.md).

T29 giữ trạng thái **Partial** cho tới khi PayOS được mở lại. T30 deployment hardening đã được hoàn tất ở đợt 17 bên dưới.

## Đợt 16 — Customer Order Center (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T31 | Lịch sử/chi tiết/hủy đơn Customer | Xong — owner-only list/detail dùng checkout snapshots; trả 404 cross-owner; hiển thị Order/Payment/Reservation/Shipment độc lập; Customer chỉ hủy trực tiếp COD `PENDING_COD_CONFIRMATION`; transaction hoàn Reserved→Available, Payment VOID, Order CANCELLED và ghi movement/cancellation evidence/audit/notification; replay idempotent; race với Staff confirm chỉ một nhánh thắng. React `/orders` hoàn chỉnh. |

Bằng chứng đợt 16: **114 BE integration tests pass** (110 cũ + 4 Customer order ownership/cancel/concurrency), **48 FE tests pass** (46 cũ + 2 history/detail/cancel); .NET build 0 warning/error, React build và lint pass. Contract: [api/customer-orders.md](api/customer-orders.md), kế hoạch/kết quả: [superpowers/plans/2026-10-30-customer-order-center.md](superpowers/plans/2026-10-30-customer-order-center.md).

T31 là feature bổ sung độc lập theo yêu cầu sau T29; không thay đổi quyết định hoãn PayOS.

## Đợt 17 — Deployment readiness, health, logging và recovery (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T30 | Deployment/health/logging/backup | Xong — anonymous liveness/readiness tách riêng; PostgreSQL readiness không lộ secret; correlation ID xuyên response/ProblemDetails/log scope; JSON console logs UTC + Activity IDs; startup fail-fast thiếu connection; Data Protection keys persist; trusted proxy allowlist; graceful shutdown; API image chạy non-root, container filesystems read-only, React/Nginx same-origin proxy, Compose smoke stack; numbered-SQL release policy và backup/restore scripts có target/hash/empty-DB guards. |

Bằng chứng đợt 17: **116 BE integration tests pass** (114 cũ + 2 operations health/correlation), **48 FE tests pass**; .NET build 0 warning/error, React test/build/lint pass. `docker compose config` và PowerShell parser pass. Runbook: [operations/deployment.md](operations/deployment.md), [operations/backup-recovery.md](operations/backup-recovery.md).

T30 không tuyên bố HA, RPO/RTO hay benchmark khi chưa có hạ tầng/drill thật. Bước nghiệp vụ độc lập tiếp theo là **T23 — Flash Sale campaign/offer**.

## Đợt 18 — Flash Sale campaign/offer (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T23 | Flash Sale campaign/offer | Xong — Admin-only quản trị campaign & offers; DRAFT/SCHEDULED/ACTIVE/ENDED/CANCELLED; hosted worker lưu bền `SCHEDULED → ACTIVE → ENDED` và ghi audit; public/checkout đánh giá theo clock trong polling gap; Publish/Cancel khóa campaign; Cancel theo **SM-Q02** chỉ dừng checkout mới và bảo toàn đơn cũ; React Admin Flash Sale panel. |

Bằng chứng đợt 18: **121 BE integration tests pass** (116 cũ + 5 Flash Sale RBAC/validation/invariants/publish-cancel/concurrency), **50 FE tests pass** (48 cũ + 2 Flash Sale admin UI/lifecycle); .NET build 0 warning/error, React build và lint pass. Contract: [api/flash-sale.md](api/flash-sale.md), kế hoạch: [superpowers/plans/2026-10-30-flash-sale-campaign-offer.md](superpowers/plans/2026-10-30-flash-sale-campaign-offer.md).

Bước nghiệp vụ tiếp theo là **T24 — Flash Sale checkout allocation (HELD+SOLD, per-customer limit, concurrency tests)**.

## Đợt 19 — Flash Sale checkout allocation (hoàn tất 30/10/2026)

| ID | Task | Kết quả |
|---|---|---|
| T24 | Flash Sale checkout allocation | Xong — Tích hợp Flash Sale lines vào checkout transaction; lock order thống nhất `Customer → Campaign → Offer → SKU → InventoryBalance`; cancel/checkout tranh chấp cùng campaign lock; giá `sale_price`, quota/per-customer guard và allocation `HELD → SOLD/RELEASED`; offer đi xuyên banner → product → cart → checkout và được lưu bởi migration 003; high-concurrency quota không oversell. |

Bằng chứng sau independent review/hardening: **132 BE integration tests pass**, **51 FE tests pass**; bổ sung lifecycle worker, effective-time checkout, cancel/checkout serialization, downstream lock-order concurrency và cart/storefront end-to-end. Contract: [api/checkout.md](api/checkout.md), [api/flash-sale.md](api/flash-sale.md), [api/cart.md](api/cart.md); kế hoạch sửa lỗi: [superpowers/plans/2026-10-30-flash-sale-hardening.md](superpowers/plans/2026-10-30-flash-sale-hardening.md); quyết định COD: [ADR 0004](adr/0004-enable-cod-for-flash-sale-while-payos-deferred.md).

## Các đợt tiếp theo

| ID | Task | Phụ thuộc / tiêu chí |
|---|---|---|
| T19 | Kiểm chứng PayOS sandbox và ADR integration | **Deferred — ADR 0003**; cần sandbox credential và bằng chứng contract |
| T20 | PayOS attempts và webhook | **Deferred — ADR 0003**; phụ thuộc T19 |
| T21 | Payment/expiry/cancel race | **Deferred — ADR 0003**; phụ thuộc T19–T20 |
| T22 | Reconciliation/refund thủ công | **Deferred — ADR 0003**; phụ thuộc T19–T21 |
| T23 | Flash Sale campaign/offer | **Xong** — Admin quản trị quota, giá, lịch; cancel chỉ chặn mua mới |
| T24 | Flash Sale checkout allocation | **Xong** — HELD+SOLD, per-customer limit, concurrency tests |
| T29 | AC-20a/b/c | **Partial:** AC-20a pass; AC-20b/c deferred theo ADR 0003 |
| T30 | Deployment/health/logging/backup | **Xong** — health/logging/container/secrets/migration + recovery runbook; benchmark chốt riêng |

Các mục OPEN trong README vẫn giữ nguyên cho đến khi có quyết định. Task trước không tự phê duyệt quyền của task sau.
