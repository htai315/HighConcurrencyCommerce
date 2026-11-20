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

## Quyết định roadmap — PayOS được mở lại có kiểm soát (cập nhật 09/11/2026)

[ADR 0003](adr/0003-defer-payos-complete-cod-first.md) ghi lại quyết định hoãn ban đầu. T19–T22 hiện đã có runtime implementation, nhưng `PAYOS_ENABLED` vẫn mặc định `false`; chỉ được bật sau khi sandbox smoke, callback thật, provider paid-query contract và release gate trong [runbook PayOS](operations/payos.md) đạt yêu cầu.

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

## Đợt 20 — Đăng nhập Google và Quản lý External Identities (hoàn tất 01/11/2026)

| ID | Task | Kết quả |
|---|---|---|
| T32 | Google Sign-In & External Identities | Xong — Google Identity Services (GIS) Web SDK + JS callback; server-side nonce challenge HttpOnly bảo vệ chống replay; bảng `external_identities` và `auth_challenges`; `password_hash` nullable cho tài khoản Google-only (chặn password login nếu không có password); kiểm tra và chặn auto-linking khi trùng email (`ACCOUNT_LINK_REQUIRED`); liên kết/hủy liên kết Google trong Bảo mật tài khoản có xác thực lại mật khẩu; chặn hủy phương thức đăng nhập cuối (`LAST_AUTH_METHOD`); thu hồi toàn bộ session khi hủy liên kết; retry loop chống race condition Postgres unique violation (23505); audit log đầy đủ; React UI với `GoogleSignInButton` trên Login/Register và quản lý phương thức đăng nhập tại `/account/security`. |

Bằng chứng đợt 20: **145 BE integration tests pass** (132 cũ + 13 Google Auth tests chuyên sâu), **57 FE tests pass** (52 cũ + 5 Google Auth UI/methods tests); .NET solution build 0 warning/error, React build và lint (oxlint) pass 0 warning/error. Tài liệu: [ADR 0002](adr/0002-google-authentication.md), [auth contract](api/auth.md).

## Các đợt tiếp theo

### T32 — Sửa lỗi sau review độc lập (01/11/2026)

- Sửa atomic challenge consumption, EF tracking sau rollback/retry, lock order user → identity và recheck identity sau chờ khóa.
- Khôi phục form đổi mật khẩu Staff/Admin; dùng chung GIS component cho login/link, không gọi challenge theo ký tự mật khẩu; thêm retry với challenge mới, SDK timeout/retry, guard callback trùng và unmount.
- Bổ sung rate limit re-auth cho link/unlink.
- Backend: 149/149 tests đạt. Có barrier buộc hai registration đến bước insert trước khi save; test 8 request replay cùng challenge; test giữ khóa user khi unlink để ép login chờ; HTTP tests rate limit POST/DELETE.
- Browser regression `frontend/scripts/google-auth-regression-smoke.mjs` đạt cho Staff/Admin/Customer với API/GIS fixtures. Không dùng credential thật và không coi mock là Google smoke thật.
- Frontend: 62/62 tests đạt bằng `npm test -- --maxWorkers=2`, gồm role nội bộ, tải SDK ở trang Bảo mật trực tiếp, mật khẩu không thay challenge, retry, callback trùng/unmount và expiry. Lượt chạy full song song với backend trước đó có 2 lỗi ở test catalog/product-management (assertion thứ tự request và timeout); chạy lại toàn bộ với 2 workers đạt, không sửa test nghiệp vụ cũ để bỏ qua lỗi. Build/lint đạt; Vite vẫn cảnh báo bundle trên 500 kB như trước sửa.
- Graphify cập nhật thành công: 4198 nodes, 9557 edges, 327 communities. Bộ phân tích SQL chưa được cài, 5 file SQL không đóng góp symbols vào đồ thị.
- Google OAuth smoke thật vẫn chờ Client ID/cấu hình môi trường hợp lệ; không đánh dấu phần này đã nghiệm thu.

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
| T33 | Quên mật khẩu + xác minh email | **Xong** — Token một lần chỉ lưu hash (SHA-256), payload outbox mã hóa Data Protection, email anti-enumeration (202 Accepted đồng nhất), xác minh bắt buộc trước khi đăng nhập mật khẩu, background delivery worker theo mô hình short lease-claim (`locked_at`, `locked_by`), gửi SMTP hoàn toàn ngoài DB transaction, sanitize error code `<= 64` ký tự, migration 005 idempotent không tự verified email chưa xác minh khi chạy lại, link dùng fragment `#token=...` chống rò rỉ, lock order User -> Token đồng nhất toàn hệ thống loại trừ deadlock, chặn tài khoản `INVITATION_PENDING` bypass qua reset password, đầy đủ trang ForgotPassword, ResetPassword, VerifyEmail (không lộ email query param, lưu qua sessionStorage), cấu hình Mailpit Docker Compose, và bộ integration/frontend tests toàn diện. Kế hoạch: [2026-11-03-password-reset-email-verification.md](superpowers/plans/2026-11-03-password-reset-email-verification.md) |
| T34 | Quản trị tài khoản nội bộ/Staff | **Xong** — Admin tìm kiếm/lọc tài khoản (Customer, Staff, Admin), phân trang an toàn chống tràn số nguyên, mời Staff qua email token 24h dùng 1 lần (SHA-256), không có default password, Staff tự kích hoạt và đặt mật khẩu qua fragment `#token=...`, Admin gửi lại lời mời (hủy token cũ, cooldown 60s), khóa/mở tài khoản, thu hồi toàn bộ session nhân sự nghỉ việc; thứ tự khóa chuẩn hóa User -> Token loại trừ deadlock giữa Resend và Accept; cấm tạo Admin qua API, cấm chuyển Customer thành Staff, cấm Admin tự khóa hoặc tự thu hồi chính mình; Audit Log giao dịch đầy đủ, rate limiting chuyên dụng; workspace "Tài khoản & Nhân sự" trong Admin Dashboard và trang public kích hoạt Staff; bộ tests backend concurrency (17/17) và frontend (9/9) toàn diện. Kế hoạch: [2026-11-03-internal-account-staff-management.md](superpowers/plans/2026-11-03-internal-account-staff-management.md) |
| T35 | E2E Testing & Release Gate | **Xong** — Bộ Playwright E2E gồm 3 specs (01-auth-lifecycle, 02-staff-invitation, 03-commerce-fulfillment) phủ 7 luồng cốt lõi tích hợp Mailpit REST API thật để trích xuất token `#token=...` không dùng mock; Staff invitation E2E đồng bộ 100% với UI và subject email Mailpit thực tế; Commerce E2E trích xuất mã đơn `orderNumber` cụ thể, loại bỏ toàn bộ if-guard, tự động reload hàng đợi fulfillment qua callback `onConfirmed` và `refreshKey` kèm nút "Làm mới" thủ công; assertion trạng thái shipment dùng `data-testid="shipment-status"` không xung đột strictness; bắt buộc từng bước tuần tự (CONFIRMED -> PENDING_PACKING -> PACKED -> SHIPPING -> DELIVERED & thu COD với mã biên nhận duy nhất `REF-E2E-...`) và kiểm tra order card / notification gắn đúng mã đơn; cách ly dữ liệu test tuyệt đối: các luồng password reset và commerce tạo tài khoản Customer độc lập, không đổi mật khẩu demo và không chạm trần pending COD khi Playwright retry; Google UI contract smoke kiểm tra contract providers, selector thực tế (`.google-auth-wrapper`, `.google-button-container`), thuộc tính `data-client-id` và tuyệt đối không rò rỉ secret; Nginx proxy chính xác location `/health/` tới API backend; Bảo vệ Production seed guard đa tầng: CLI `--seed-demo` yêu cầu đồng thời `DemoSeed:Enabled=true` VÀ môi trường không phải Production (`Development`/`Testing`/`Local`), cấu hình `DemoSeed__Enabled` trong `compose.demo.yaml` và base `compose.yaml` sạch 100%; script `demo-up.ps1`/`demo-up.sh` và CI chủ động seed idempotent sau readiness; GitHub Actions CI workflow (`.github/workflows/ci.yml`) đa tầng chặn merge nếu fail backend (197 tests), frontend (81 tests, lint, production build), docker compose config hoặc E2E, có kiểm tra timeout và exit 1 rõ ràng; Security hardening với middleware headers bảo mật (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`) và Nginx CSP tối ưu cho Google GIS; tài liệu Release Checklist và E2E Testing Guide chi tiết. Kế hoạch: [2026-11-03-e2e-testing-and-release-gate.md](superpowers/plans/2026-11-03-e2e-testing-and-release-gate.md) |
| T36 | Đánh giá & nhận xét sản phẩm (Reviews & Ratings) | **Xong** — Một active review/Customer/Product; điều kiện nghiêm ngặt chỉ đơn COMPLETED + shipment DELIVERED; rating 1–5 sao + nhận xét 10–2.000 ký tự; aggregate trực tiếp từ SQL source-of-truth; owner-only edit và soft-delete (cho phép viết lại sau khi xóa); Staff/Admin kiểm duyệt hide (bắt buộc lý do 3–500 ký tự)/restore ghi nhận Audit Log trong cùng transaction; concurrency guard với partial unique index PostgreSQL `ux_product_reviews_active_customer_product` (`WHERE deleted_at IS NULL`) và bắt `23505` unique violation; che tên người đánh giá hiển thị công khai (e.g. `N***n A*`); API public catalog, API Customer và API moderation; giao diện Storefront (ProductCard, PDP, Orders) và Back Office (Staff & Admin Dashboard tab "Kiểm duyệt Đánh giá"); 29 integration tests backend, 8 test groups frontend (89 tests) và spec Playwright E2E 04-product-reviews toàn diện. Kế hoạch: [2026-11-04-product-reviews-ratings.md](superpowers/plans/2026-11-04-product-reviews-ratings.md) |
| T37 | Danh sách yêu thích (Wishlist) | **Xong** — Customer lưu theo Product (tối đa 500 sản phẩm); PUT/DELETE lũy đẳng (204 No Content); kiểm tra sản phẩm mở bán `PUBLISHED` và SKU `ACTIVE`; phân loại 3 trạng thái tồn kho `AVAILABLE`, `OUT_OF_STOCK`, `PRODUCT_UNAVAILABLE` (placeholder sản phẩm ngừng bán, cho phép bỏ lưu); transaction Repeatable Read cho phân trang nhất quán; concurrency lock `user_accounts FOR NO KEY UPDATE` chống vượt trần 500; bảo vệ CSRF bắt buộc cho mọi mutation; shared state được scope theo `user.id` và loại bỏ stale response; Guest giữ `returnTo`; dialog nhiều SKU bắt buộc chọn rõ ràng, hỗ trợ focus trap/Escape/focus return; tự điều chỉnh trang cuối; 10 integration tests backend (236/236 pass toàn suite), 9 unit tests frontend (98/98 pass), oxlint 0 lỗi 0 cảnh báo, build pass, và Playwright E2E thật `05-wishlist.spec.ts`. Kế hoạch: [2026-11-04-wishlist.md](superpowers/plans/2026-11-04-wishlist.md) |
| T38 | Mã giảm giá cơ bản (Basic Vouchers) | **Xong** — Voucher cố định/phần trăm, subtotal tối thiểu, tổng lượt/per-customer/budget; allocation HELD nguyên tử tại checkout, CONSUMED khi COD hoàn tất, RELEASED khi cancel/expiry/return; lock order chuẩn hóa loại trừ deadlock; bảo vệ tương thích lũy đẳng checkout-v1/v2; snapshot subtotal/discount/code trên Order; Admin lifecycle + Audit, Cart/Order UI và E2E thật 100% pass trên Docker demo stack. Kế hoạch: [2026-11-04-basic-vouchers.md](superpowers/plans/2026-11-04-basic-vouchers.md) |
| T39 | Đổi/trả và hoàn tiền thủ công đơn COD | **Xong** — Customer tạo một yêu cầu theo item/quantity trong 7 ngày sau giao; Admin duyệt/từ chối và ghi chứng từ hoàn tiền thủ công; Staff nhận hàng/hoàn kho exactly-once; partial/full refund tính theo giá thực trả sau Flash Sale + voucher (Remainder 1 VNĐ phân bổ chuẩn xác theo Item Id, từ chối dòng có net bằng 0 thay vì tự sinh 1 VNĐ); lock order chống deadlock (SalesOrder -> OrderReturnRequest -> Payment -> ProductSku -> InventoryBalance -> ProductReview); voucher CONSUMED và Flash quota SOLD không hoàn trả; tích hợp Payment (COD_COLLECTED -> REFUND_REQUIRED -> REFUNDED/PARTIALLY_REFUNDED), kho (RETURN_IN), ẩn review khi 0 đơn vị sở hữu, báo cáo tài chính (Gross/Refund/Net Revenue, cho phép Net âm theo kỳ) và Audit Log; UI đầy đủ Customer/Staff/Admin; 283 backend integration tests, 110 frontend tests, build pass, API docs và kịch bản Playwright E2E `07-cod-returns-refunds.spec.ts`. Kế hoạch: [2026-11-05-cod-returns-manual-refunds.md](superpowers/plans/2026-11-05-cod-returns-manual-refunds.md) |
| T40 | Versioned Database Migration & Deployment Gate | **Xong** — CLI `--migrate-database`; embedded manifest 001–011 (seed 007 tách riêng); `public.schema_migrations` lưu SHA-256 checksum/version/execution mode; advisory lock tuần tự hóa toàn cụm; legacy baseline riêng 001; fail-fast khi thiếu migration, checksum drift hoặc binary downgrade; Compose one-shot migrator chặn API đến khi schema hoàn tất; integration tests phủ fresh DB, legacy upgrade, concurrent runners, failure recovery và drift guards. ADR: [0007](adr/0007-versioned-database-migration-runner.md). Kế hoạch: [2026-11-05-versioned-database-migrations.md](superpowers/plans/2026-11-05-versioned-database-migrations.md) |

Các mục OPEN trong README vẫn giữ nguyên cho đến khi có quyết định. Task trước không tự phê duyệt quyền của task sau.

## Đợt 30 — T41 Kiểm định và cách ly hàng đổi/trả (05/11/2026)

| ID | Task | Kết quả |
|---|---|---|
| T41 | Returned Goods Quality Inspection & Quarantine | Hoàn tất — migration 012; receive vào quarantine; Staff phân loại RESTOCK/QUARANTINE/DAMAGED/SCRAPPED; invariant tồn kho 5 phân vùng; idempotency + concurrency guard; Admin chỉ refund sau INSPECTED; UI và E2E cập nhật. **290/290 BE**, **111/111 FE**, lint/build pass, Docker migration và Playwright flow 07 pass. |

ADR: [0008](adr/0008-returned-goods-inspection-and-quarantine.md). Kế hoạch: [2026-11-05-return-quality-inspection.md](superpowers/plans/2026-11-05-return-quality-inspection.md). API: [returns.md](api/returns.md).

## Đợt 31 — T42 Đổi hàng trực tiếp (05/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T42 | Direct Product Exchange & Replacement Shipment | **Hoàn tất** — đổi SKU cùng Product; Admin giữ tồn target nguyên tử 7 ngày; worker giải phóng allocation hết hạn; hàng cũ qua receive/inspection T41; kiện thay thế có pack/handover/delivery/retry/return-stock; Payment/voucher/Flash Sale giữ nguyên; UI Customer/Staff/Admin và integration test xuyên suốt. **295/295 BE**, **115/115 FE**, lint/build/Compose pass; Playwright flow 08 chạy xanh trên Docker thật. |

ADR: [0009](adr/0009-direct-product-exchange-and-replacement-shipment.md). Kế hoạch: [2026-11-05-direct-product-exchange.md](superpowers/plans/2026-11-05-direct-product-exchange.md). API: [exchanges.md](api/exchanges.md).

### T42.1 — Direct Exchange Hardening & Release Acceptance (06/11/2026)

**Hoàn tất.** Đã bổ sung kiểm thử PostgreSQL thật cho approval tranh slot tồn cuối, inspection/handover replay, giới hạn giao lại, return-stock và expiry exactly-once; UI hiển thị resolution badge, source → target, tác động giữ tồn và timeline đổi hàng; Playwright flow 08 chạy xuyên suốt Customer → Admin → Staff → Customer trên Docker demo stack. Release gate: **295/295 backend**, **115/115 frontend**, oxlint/build/Compose pass. Kế hoạch: [2026-11-06-direct-exchange-hardening.md](superpowers/plans/2026-11-06-direct-exchange-hardening.md).

## Đợt 32 — T43 Quản trị danh mục sản phẩm (06/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T43 | Category Management & Storefront Taxonomy | **Hoàn tất** — Admin tạo/sửa/ẩn/hiện/xóa danh mục trống; slug chuẩn hóa và unique concurrency trả 409; danh mục đang có sản phẩm không được xóa; Audit Log transactional; Staff đọc taxonomy nội bộ và gắn danh mục khi tạo/sửa Product Draft; Storefront chỉ hiển thị danh mục visible trong navigation/filter nhưng không ẩn sản phẩm khi category bị ẩn. **299/299 BE**, **117/117 FE**, lint/build/Compose pass; Playwright flow 09 chạy xanh trên Docker thật. |

Kế hoạch: [2026-11-06-category-management.md](superpowers/plans/2026-11-06-category-management.md). API: [categories.md](api/categories.md).

## Đợt 33 — T44 Thương hiệu và thông số sản phẩm (06/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T44 | Brand Management & Product Specifications | **Hoàn tất** — Admin CRUD/kích hoạt thương hiệu, slug unique chống race và guard không xóa khi đang dùng; Staff đọc hãng active, gắn hãng và quản lý thông số `snake_case` trên Product Draft; khóa hàng Product tuần tự hóa Staff mutation với Publish; Catalog công khai hỗ trợ brand filter, brand metadata và bảng thông số; UI Admin/Staff/Storefront/PDP, integration và component tests đầy đủ. **303/303 BE**, **119/119 FE**, lint/build/Compose pass. |

Kế hoạch: [2026-11-06-brand-and-product-specifications.md](superpowers/plans/2026-11-06-brand-and-product-specifications.md). API: [brands-product-specifications.md](api/brands-product-specifications.md).

## Đợt 34 — T45 Upload và lưu trữ ảnh sản phẩm (06/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T45 | Product Media Upload & Object Storage Boundary | **Hoàn tất** — Staff upload đa ảnh JPEG/PNG/WebP tối đa 8 MiB; kiểm tra magic bytes; tên object UUID bất biến; filesystem volume cho Docker demo qua abstraction `IProductMediaStorage`; public media endpoint có cache immutable/range/ETag; Draft-only metadata/reorder/delete tuần tự hóa với Publish; cleanup file khi DB thất bại; UI drag/drop, alt text, reorder và xóa. **306/306 BE**, **119/119 FE**, lint/build/Compose pass; Playwright flow 10 chạy xanh trên Docker thật và file vẫn đọc được sau khi recreate API container. |

Kế hoạch: [2026-11-06-product-media-upload.md](superpowers/plans/2026-11-06-product-media-upload.md). API: [product-media.md](api/product-media.md).

## Đợt 35 — T47 Product Revision Workflow (kế hoạch 06/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T47 | Product Revision Workflow | **Hoàn tất implementation chính** — migration 015 + ADR 0010; snapshot content/media/spec; `content_version`; one-open-revision partial unique index; lock order Product → Revision; Staff create/edit/upload/reorder media/spec/submit/cancel; Admin queue/structured summary/approve/reject; approve nguyên tử và không đổi SKU/giá/tồn kho. **310/310 BE**, **121/121 FE**, lint/build/Compose pass; migration 015 và Playwright flow 11 chạy xanh trên Docker thật. Hardening tiếp theo có thể mở rộng diff từng ảnh/thông số và fault-injection rollback. |

Kế hoạch: [2026-11-06-product-revision-workflow.md](superpowers/plans/2026-11-06-product-revision-workflow.md). ADR: [0010](adr/0010-product-revision-workflow.md). API: [product-revisions.md](api/product-revisions.md).

## Đợt 36 — T46 Product Lifecycle Management (kế hoạch 06/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T46 | Product Lifecycle Management | **Hoàn tất** — migration 016 + ADR 0011; `SUSPENDED`; Admin suspend/resume/archive; archive terminal; lock order Customer → Voucher → Product → Flash Sale → SKU → Inventory; Cart/Wishlist giữ placeholder; T47 cho sửa Product suspended nhưng approve không resume; archive guard revision/Flash Sale; Audit Log transactional. **316/316 BE**, **122/122 FE**, lint/build/Compose pass; migration 016 và Playwright flows 11–12 chạy xanh trên Docker thật. |

Kế hoạch: [2026-11-06-product-lifecycle-management.md](superpowers/plans/2026-11-06-product-lifecycle-management.md). ADR: [0011](adr/0011-product-lifecycle-management.md). API: [product-lifecycle.md](api/product-lifecycle.md).

## Đợt 37 — T47.1 Product Revision Hardening (kế hoạch 06/11/2026)

| ID | Task | Trạng thái / phạm vi |
|---|---|---|
| T47.1 | Product Revision Hardening & Review Workspace | **Hoàn tất** — migration 017 + ADR 0012; structured diff live/proposed cho content/gallery/spec; stale/lifecycle guard; durable media cleanup queue có short lease/retry; rollback fault-injection tại bốn checkpoint approval; concurrency Approve ↔ Suspend; Admin review workspace ba tab và cleanup placeholder. Release gate: **326/326 BE**, **123/123 FE**, lint/build sạch; 20/20 migration + revision hardening tests và Playwright Flow 13 pass trên Docker demo thật. |

Kế hoạch: [2026-11-06-product-revision-hardening.md](superpowers/plans/2026-11-06-product-revision-hardening.md). Sau khi hoàn tất mới mở T48 Shipping Methods & Fees.

## Đợt 38 — T48 Shipping Methods & Fees (kế hoạch 06/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T48 | Phương thức vận chuyển & tính phí | **Hoàn tất T48.1–T48.9** — migration/calculator/Admin/quote API; checkout-v3 khóa `Customer→Shipping→Voucher→Product→Flash Sale→SKU→Inventory`, revalidate revision/phí, snapshot method/ETA/tỉnh, cộng phí vào Order/Payment, zero-leak khi stale và replay tương thích v1/v2; Order/COD/Fulfillment trả snapshot vận chuyển, return không hoàn phí ship, reporting tách `shippingRevenue` nhưng không cộng trùng Gross/Net; React Cart tự quote theo address/cart, chọn method, hiển thị phí/ETA, xử lý stale không auto-retry; Admin Shipping workspace quản lý CRUD, activate/deactivate/default, ETA/threshold và phụ phí tỉnh với expectedRevision + stale reload. Demo seed có active default idempotent. Release gate: **357/357 BE**, **130/130 FE**, lint/build/Compose sạch; Playwright Flow 14 và regression Flow 3, 6, 7, 11–13 pass trên Docker demo thật. |

Kế hoạch: [2026-11-06-shipping-methods-fees.md](superpowers/plans/2026-11-06-shipping-methods-fees.md). T48 không gọi hãng vận chuyển bên ngoài; tracking/label/webhook carrier sẽ được tách sang task tiếp theo sau khi phí nội bộ ổn định.

## Đợt 39 — T49 Carrier Consignments & Internal Tracking (kế hoạch 07/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T49 | Vận đơn, nhãn và tracking nội bộ | **Hoàn tất T49.1–T49.8** — backend data/storage/Admin/Staff/fulfillment/customer orchestration giữ nguyên các invariant owner-only, safe projection, HTTPS URL và notification exactly-once. React có Admin carrier registry với revision/stale reload; Staff tạo vận đơn sales/exchange, tải nhãn, bàn giao, hủy và ghi event; Customer Order Center hiển thị carrier, tracking number, external HTTPS link an toàn và timeline lazy-load. Staff dùng endpoint chỉ đọc carrier `ACTIVE`, không hard-code ID hoặc gọi API Admin. Release gate: backend **375/375**, frontend **134/134**, lint/build/Compose sạch; Playwright Flow 15 và regression Flow 3, 7, 8, 14 pass trên Docker demo thật. |

Kế hoạch: [2026-11-07-carrier-consignments-tracking.md](superpowers/plans/2026-11-07-carrier-consignments-tracking.md). PayOS vẫn deferred theo ADR 0003.

## Đợt 40 — T50 Advanced Product Search & Discovery (kế hoạch 07/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T50 | Tìm kiếm và khám phá sản phẩm nâng cao | **Hoàn tất T50.1–T50.9** — PostgreSQL-native search, facets, autocomplete, responsive URL-driven UX và performance gate 10.000 Product/30.000 SKU. Public query timeout 2 giây; năm case search/suggestion dùng GIN, không seq scan search tables. Flow 16 chạy thật trên Docker: không dấu, exact SKU, category/brand/stock/price/rating, sort/back-forward, mobile focus/Escape và suspend/resume visibility. Regression Flow 4, 5, 11–14 xanh. Release gate: backend **415/415**, frontend **145/145**, Playwright **18 tests/16 specs**, lint/build/Compose sạch; CI tự đếm migration và chạy query-plan benchmark. |

Kế hoạch: [2026-11-07-advanced-product-search-discovery.md](superpowers/plans/2026-11-07-advanced-product-search-discovery.md). PayOS tiếp tục deferred theo ADR 0003 cho tới khi có sandbox credentials và verified provider contract.

## Đợt 41 — T51 Wishlist Product Alerts (kế hoạch 07/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T51 | Cảnh báo có hàng lại và giảm regular price từ Wishlist | **Hoàn tất T51.1–T51.8** — pipeline và UI đã qua hardening/fault injection: concurrent remove/update tuần tự hóa; worker revalidate khi item bị xóa sau snapshot; nhiều SKU coalesce một business alert; reservation→0 chỉ cập nhật baseline, release phát đúng một lần; price increase/replay, Flash Sale và suspend không phát sai; resume/new low phát đúng transition. Evaluator/SMTP cố release lease tối đa 5 giây khi shutdown, expiry vẫn fallback. Frontend chặn email chưa verified và không nhận stale response sau account switch. Flow 17 chạy thật trên Docker/PostgreSQL/Mailpit, chứng minh in-app + email exactly-once cho stock-in/price-drop, reload không nhân bản và opt-out chặn sự kiện tiếp theo. Regression Flow 1, 5, 8, 11, 12, 16 xanh; Flow 8 dùng fixture Product hai SKU cố định thay vì phụ thuộc thứ tự Catalog. Runbook vận hành có truy vấn backlog/lease/error, ngưỡng cảnh báo, triage và recovery an toàn; CI bắt buộc discover Flow 17 rồi chạy Flows 1–17. Release: backend **441/441**, frontend **151/151**, Playwright **19 tests/17 specs**, lint/build và Compose validation sạch. |

Kế hoạch: [2026-11-07-wishlist-product-alerts.md](superpowers/plans/2026-11-07-wishlist-product-alerts.md). API: [wishlist-alerts.md](api/wishlist-alerts.md). Vận hành: [wishlist-product-alerts.md](operations/wishlist-product-alerts.md). PayOS tiếp tục deferred theo ADR 0003.

## Đợt 42 — T52 Product Comparison (kế hoạch 08/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T52 | So sánh tối đa bốn sản phẩm cùng danh mục | **Hoàn tất T52.1–T52.7** — batch API giữ order/public projection/category guard, đúng một DB command và timeout 2 giây có restore. Demo seed có 6 key/12 dòng spec idempotent. Frontend có storage/context/button/tray, GUID parser khớp .NET/PostgreSQL, URL-source-of-truth `/compare`, strict response/image sanitization. Matrix semantic/responsive có core + union specs; remove giữ filter và focus. xUnit conservative tối đa 4 collection threads ổn định release gate. Flow 18 pass Docker thật cho persist/share/matrix/category/lifecycle omission; regressions 5/12/16/17 đạt 4/4. **Backend 455/455; frontend 162/162; Playwright 20 tests/18 specs; lint/build và base+demo Compose sạch**. Không migration, recommendation/ranking hoặc Flash Sale price. |

Kế hoạch: [2026-11-08-product-comparison.md](superpowers/plans/2026-11-08-product-comparison.md). API: [product-comparison.md](api/product-comparison.md). Thứ tự triển khai T52.1–T52.7; PayOS tiếp tục deferred theo ADR 0003.

## Đợt 43 — T53 Related Products & Recently Viewed (08/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T53 | Sản phẩm tương tự và lịch sử đã xem | **Hoàn tất T53.1–T53.8 + audit hardening + nghiệm thu Docker thật** — Related V1 public API trả tối đa 6 sản phẩm cùng category/public với ranking deterministic (in-stock → same active brand → regular-price distance → published rating → review count → ID tie-break); `<= 2` DB commands và timeout 2s có restore; không leak internal rank score hay inventory partition. `Guid.Empty` được xử lý generic 404, hai endpoint có `ResponseCache(NoStore)` và parser frontend kiểm tra UUID/count/order/duplicate/rating/image URL nghiêm ngặt. Lịch sử xem lưu hoàn toàn client-side trong `localStorage` (`hcc.recent-products.v1`), TTL 30 ngày, cap 12, dedupe, loại timestamp tương lai quá 5 phút, đồng bộ xóa giữa tab và prune omitted ID mà không xóa nhầm PDP hiện tại. UI `RelatedProductsSection` và `RecentlyViewedSection` tái sử dụng `ProductCard`, xử lý loading skeleton/error retry/clear history một chạm/a11y/reduced motion; không chặn PDP/Storefront khi API lỗi. Selector Flow 19 dùng accessible name chính xác; Flow 18 tìm fixture qua query nên không phụ thuộc thứ tự/phân trang sau các flow tạo dữ liệu. Audit concurrency bổ sung test Review ↔ Full Refund có điều phối khóa thật và chuyển khóa Customer của Review sang `FOR NO KEY UPDATE`, loại bỏ deadlock User → Order / Order → User mà vẫn tuần tự hóa tạo review. **Backend toàn bộ 480/480 (T53 24/24); frontend 178/178; Playwright Flow 19 + regressions 5/12/16/17/18 đạt 6/6 trên Docker; oxlint 0 lỗi 0 cảnh báo; build và base+demo Compose sạch**. Không migration, không server-side tracking/sync. |

Kế hoạch: [2026-11-08-related-products-recently-viewed.md](superpowers/plans/2026-11-08-related-products-recently-viewed.md). API: [product-discovery.md](api/product-discovery.md). PayOS tiếp tục deferred theo ADR 0003.

## Đợt 44 — T19–T22 PayOS (kế hoạch 09/11/2026)

| ID | Công việc | Trạng thái |
|---|---|---|
| T19 | Kiểm chứng contract PayOS và ADR mở lại | **Đang thực hiện** — đã nhận credential ngoài repository; create/query/cancel, invalid request signature và official SDK `payOS 2.1.0` trên .NET 10 đã pass, bằng chứng đã sanitize. Còn webhook/payment-success, ACK/retry, timeout, duplicate/late/amount mismatch và cancel-vs-paid trước khi ADR 0017 được Accepted. |
| T20 | Payment attempts, webhook và Customer payment UI | **Backend foundation và Customer UI hoàn tất, feature vẫn tắt** — migration 022; sequence/order-code guard; một active attempt; official SDK 2.1.0 `MaxRetries=0`; hai transaction ngắn bao quanh provider call; replay link; owner-only status; webhook durable/deduplicated. Cart chỉ mở lựa chọn qua capability endpoint không lộ secret, tạo/replay link và chuyển hướng đúng host `pay.payos.vn`; trang return chỉ tin trạng thái backend. API: [payos.md](api/payos.md). |
| T21 | Payment/expiry/cancel race và PayOS fulfillment | **Runtime backend/frontend hoàn tất, production gate còn mở** — shared transition giữ lock order `Order -> Payment -> Attempt -> Voucher -> Flash Sale -> SKU -> Inventory`; verified webhook áp SUCCESS/ALLOCATED exactly-once; mismatch/late-paid mở reconciliation; cancel/expiry chỉ release sau provider `CANCELLED`. Customer có thể tiếp tục/hủy thanh toán từ Order Center; Staff giao đơn prepaid không nhập COD. 18 PayOS integration tests và UI tests phủ return URL không authoritative, feature flag, cancel eligibility và prepaid fulfillment. Còn sandbox E2E callback thật và provider GET `PAID` có transaction reference trước khi bật flag. |
| T22 | Reconciliation, manual refund và production rollout | **Runtime workflow đã harden, sandbox rollout gate còn mở** — migration 023/024 bổ sung assignment lease và liên kết Case -> Transaction -> ManualRefund; Admin queue/detail/claim/takeover/resolve/manual-refund; mismatch refund đóng Order và release tồn nguyên tử; duplicate collection tạo case riêng nhưng giữ Payment gốc; trim validation, confirm dialog, Audit Log, Customer notification, .NET metrics và concurrent refund exactly-once. Runbook staged rollout đã có; tự động payout nằm ngoài phạm vi. Còn sandbox smoke thật, provider `PAID` fixture và cấu hình exporter/pager hạ tầng trước khi bật flag. |

Bằng chứng hardening T22 và audit toàn hệ thống 2026-11-10: **508/508 backend integration tests**, **187/187 frontend tests**, .NET build 0 warning/error, frontend lint/build và Docker Compose config pass. Audit bổ sung kiểm tra webhook PayOS thiếu chữ ký trả 400, chống request trạng thái PayOS chồng nhau, retry ngay trên trang PayOS, strict parsing cho Admin Accounts, `no-store` cho dữ liệu riêng tư và trusted Nginx proxy để rate limit theo đúng IP khách.

Kế hoạch chi tiết: [2026-11-09-payos-integration.md](superpowers/plans/2026-11-09-payos-integration.md). PayOS vẫn bị khóa bởi `PAYOS_DEFERRED` và feature flag mặc định tắt cho tới khi T19–T21 đạt release gate.

## Cập nhật kiểm chứng 05/11/2026

- Triển khai hoàn tất T39: Đổi/trả và hoàn tiền thủ công cho đơn COD (T39) theo kế hoạch [2026-11-05-cod-returns-manual-refunds.md](superpowers/plans/2026-11-05-cod-returns-manual-refunds.md):
  1. ADR 0006 (`docs/adr/0006-cod-returns-and-manual-refunds.md`) & Migration `011_cod_returns_refunds.sql`: Tạo `store.order_return_requests`, `store.order_return_request_lines`, mở rộng `store.payments(payment_status)` hỗ trợ `PARTIALLY_REFUNDED`, mở rộng `store.manual_refunds` liên kết `return_request_id` và unique index chống trùng evidence reference; kiểm tra check constraints toán học và lifecycle nghiêm ngặt.
  2. EF Core models & mapping: `OrderReturnRequest.cs`, `OrderReturnRequestLine.cs`, cập nhật `ManualRefund.cs`, `SalesOrder.cs`, `SalesOrderItem.cs`, `ProductSku.cs`, `UserAccount.cs` và `StoreDbContext.cs`.
  3. Thuật toán phân bổ tiền hoàn (`RefundAllocationCalculator`): Tính tiền hoàn chính xác trên giá thực trả sau Flash Sale và Voucher; phân bổ voucher discount theo tỷ lệ gross; xử lý remainder 1 VNĐ theo `OrderItem.Id` tăng dần; bảo toàn nguyên tắc voucher `CONSUMED` và quota Flash Sale `SOLD` không bị viết ngược. 10/10 unit/integration tests trong `RefundAllocationCalculatorTests.cs` pass.
  4. Backend Services & Controllers:
     - Customer (`PostgresOrderReturnCustomerService`, `CustomerReturnsController`): Kiểm tra điều kiện 7 ngày sau giao COD, tạo yêu cầu đổi/trả từng mặt hàng và số lượng, xem danh sách/chi tiết, hủy yêu cầu khi đang `REQUESTED`.
     - Staff (`PostgresOrderReturnStaffService`, `StaffReturnsController`): Hàng đợi `APPROVED` theo thứ tự FIFO, xác nhận nhận hàng và hoàn kho exactly-once với movement `RETURN_IN`, chuyển Payment sang `REFUND_REQUIRED`.
     - Admin (`PostgresOrderReturnAdminService`, `AdminReturnsController`): Phê duyệt hoặc từ chối yêu cầu; ghi nhận chứng từ hoàn tiền thủ công với `evidence_reference` duy nhất; chuyển Payment sang `REFUNDED` hoặc `PARTIALLY_REFUNDED`; ẩn đánh giá của sản phẩm khi số lượng sở hữu sau hoàn trả bằng 0; ghi nhận Audit Log đầy đủ.
     - Exception handling (`OrderReturnExceptionHandler`): Chuẩn hóa mã lỗi `ORDER_RETURN_NOT_FOUND`, `ORDER_RETURN_INVALID_STATUS`, `ORDER_RETURN_DUPLICATE_REFERENCE`, `ORDER_RETURN_WINDOW_EXPIRED`, v.v.
  5. Tích hợp Báo cáo & Đánh giá: Bổ sung `GrossRevenue`, `RefundAmount`, `NetRevenue` và `ApprovedReturnsCount` vào `ReportingContracts` và `PostgresReportingService`; bảo vệ điều kiện đánh giá sản phẩm không còn hợp lệ khi đã hoàn trả toàn bộ.
  6. Backend Tests: `CodReturnMigrationTests` (3/3), `RefundAllocationCalculatorTests` (10/10), `OrderReturnLifecycleTests` (7/7 covering RBAC, window, concurrency race, restock, manual refund, review hiding); toàn bộ backend suite đạt **281/281 tests pass**.
  7. Frontend React UI: Runtime parsers `returns.api.ts`, modal `CreateReturnModal` (accessible dialog, item/quantity selection, live refund preview), trang `/account/returns` (`CustomerReturnsPage`), tab "Nhận hàng trả" (`StaffReturnsPanel`) trong Staff Dashboard, tab "Đổi trả & Hoàn tiền" (`AdminReturnsPanel`) trong Admin Dashboard; tích hợp OrdersPage CTA; toàn bộ frontend suite đạt **109/109 tests pass** trên 25 test files; build production pass.
  8. API Documentation & Playwright E2E: Tài liệu `docs/api/returns.md` đặc tả toàn bộ nghiệp vụ; kịch bản Playwright E2E `frontend/e2e/07-cod-returns-refunds.spec.ts` kiểm thử toàn bộ vòng đời 7 bước từ đặt đơn COD, giao hàng, tạo yêu cầu trả, admin duyệt, staff nhận hàng hoàn kho, admin ghi chứng từ hoàn tiền, đến kiểm tra trạng thái khách hàng và báo cáo doanh thu Admin.

- Trạng thái code hiện tại: **281/281 Backend integration tests** và **109/109 Frontend tests** pass (25 test files); .NET build, React production build và type check đạt chuẩn (0 warnings, 0 errors); 7 bộ kịch bản Playwright E2E đều sẵn sàng.

- Triển khai hoàn thiện T38: Mã giảm giá cơ bản (Basic Vouchers) theo kế hoạch [2026-11-04-basic-vouchers.md](superpowers/plans/2026-11-04-basic-vouchers.md):
  1. ADR 0005 & Migration `010_basic_vouchers.sql` với bảng `store.vouchers`, `store.voucher_redemptions`, snapshot fields trên `store.sales_orders` (`subtotal_amount`, `voucher_discount_amount`, `voucher_code`), check constraints toán học nghiêm ngặt cho fixed/percentage discount và currency consistency.
  2. EF Core model mapping (`Voucher.cs`, `VoucherRedemption.cs`, snapshots trong `SalesOrder.cs`), test fixture `BasicVouchersSchema.sql` và compose mount.
  3. Quản trị Admin (`PostgresVoucherAdminService`, `AdminVouchersController`): CRUD, vòng đời DRAFT -> ACTIVE <-> PAUSED -> ENDED, Audit Log transactional đầy đủ actor_id, before_state, after_state.
  4. Trải nghiệm Customer (`PostgresVoucherCustomerService`, `CustomerVouchersController`): API preview `POST /api/v1/vouchers/preview` không giữ quota, bảo vệ CSRF và rate limiting.
  5. Checkout Allocation (`PostgresCheckoutService`): Lock order đồng nhất chống deadlock (Customer -> Voucher -> Flash Sale -> SKU -> Inventory); phân bổ HELD nguyên tử; bảo toàn tương thích lũy đẳng (`checkout-v1` cho đơn không voucher, `checkout-v2` cho đơn có voucher); zero counter leaks khi transaction checkout rollback.
  6. Vòng đời đơn hàng (`PostgresVoucherLifecycleManager`): Tích hợp trực tiếp vào Customer Order Service, COD Order Service, và Fulfillment Service; chuyển `HELD -> CONSUMED` khi thu tiền COD thành công; chuyển `HELD -> RELEASED` khi khách hủy đơn, COD hết hạn xác nhận hoặc giao thất bại hoàn kho kèm lý do phát hành chuẩn mực.
  7. Reporting & Snapshots: Mở rộng `CustomerOrderContracts`, `CodOrderContracts`, `FulfillmentContracts` và giao diện OrdersPage hiển thị rõ ràng giá gốc trước giảm, mức voucher và tổng thanh toán.
  8. Giao diện Frontend: `voucher.api.ts` chuẩn hóa parser và payload; `CartPage` tích hợp card áp dụng voucher, preview giảm giá, tự động hủy bỏ voucher khi giỏ hàng bị sửa đổi số lượng; Admin Dashboard tích hợp workspace "Mã giảm giá & Ngân sách" đầy đủ form tạo/sửa bản nháp và các nút thao tác vòng đời có modal xác nhận.
  9. Kiểm thử tự động: 25 backend integration tests trong `VoucherTests.cs` (toàn bộ suite đạt **261/261 tests pass**); 7 unit tests frontend trong `voucher.test.tsx` (toàn bộ frontend suite đạt **105/105 tests pass** qua 24 test files); oxlint đạt 0 lỗi 0 cảnh báo; production build hoàn hảo; và kịch bản Playwright E2E `06-vouchers.spec.ts` kiểm chứng toàn diện từ Admin tạo/kích hoạt mã -> Customer áp dụng tại giỏ & checkout COD -> chặn vượt perCustomerLimit -> Staff hoàn tất giao COD -> Admin kiểm tra bộ đếm `0 / 1 / 2` đã dùng đạt 100% pass trên demo stack thật.
  10. Đặc tả API hoàn chỉnh trong `docs/api/vouchers.md`.
  11. Audit hardening hoàn thiện:
      - Xử lý race tạo mã trùng đồng thời (`PostgresException 23505`) trả về `409 CONFLICT` an toàn thay vì HTTP 500.
      - Hỗ trợ trường Tên chương trình (`name`) end-to-end từ database schema, EF Core mapping, DTO, search `ILike` theo cả mã và tên, đến frontend payload.
      - Bảo vệ phân trang an toàn: `[Range(1, 100_000)]` cho `Page`, `[Range(1, 100)]` cho `PageSize`, tính offset an toàn với kiểu `long`, ngăn chặn hoàn toàn lỗi tràn số nguyên 32-bit.

- Trạng thái code hiện tại: **261/261 Backend integration tests** và **105/105 Frontend tests** pass (24 test files); .NET build, React production build và oxlint đạt chuẩn (0 warnings, 0 errors); 6 bộ kịch bản Playwright E2E đều sẵn sàng.

- Triển khai hoàn thiện T37: Danh sách yêu thích (Wishlist) theo kế hoạch [2026-11-04-wishlist.md](superpowers/plans/2026-11-04-wishlist.md):
  1. Migration `009_customer_wishlist.sql` với bảng `store.customer_wishlist_items`, khóa chính phức hợp `(customer_id, product_id)`, foreign keys và chỉ mục `ix_customer_wishlist_created`.
  2. EF Core mapping `CustomerWishlistItem.cs`, navigations trong `Product.cs`, `UserAccount.cs`, `StoreDbContext.cs`, fixture kiểm thử `CustomerWishlistSchema.sql` và mount `compose.yaml`.
  3. Service backend `PostgresWishlistService` với concurrency lock `user_accounts FOR NO KEY UPDATE`, chặn giới hạn 500 (`WISHLIST_LIMIT_REACHED` 409), kiểm tra mở bán (`PRODUCT_NOT_FOUND` 404), thao tác PUT/DELETE lũy đẳng và chiếu DTO phân trang snapshot nhất quán.
  4. Controller `WishlistController` (`GET /wishlist`, `GET /wishlist/summary`, `PUT /wishlist/items/{productId}`, `DELETE /wishlist/items/{productId}`), `WishlistExceptionHandler` và bảo vệ CSRF.
  5. 10 integration tests backend trong `WishlistTests.cs`, gồm race thật cho slot thứ 500 và migration reapply (toàn bộ suite đạt **236/236 tests pass**).
  6. Frontend React: feature API `wishlist.api.ts`, context `WishlistProvider` / `useWishlist`, nút `WishlistButton`, hộp thoại `SkuSelectModal`, trang `/wishlist`, nhúng `ProductCard`, `ProductDetailPage`, `SiteHeader` (live badge), và `AccountNavigation`.
  7. 9 frontend unit tests trong `wishlist.test.tsx`, gồm account-switch stale response và điều chỉnh trang cuối (toàn bộ suite đạt **98/98 tests pass** qua 23 files test), oxlint đạt 0 lỗi 0 cảnh báo, production build thành công.
  8. Kịch bản Playwright E2E `05-wishlist.spec.ts` phủ đầy đủ luồng từ Guest redirect, đăng ký/đăng nhập, lưu catalog/PDP, xem wishlist, thêm giỏ qua modal, đến bỏ lưu.
  9. Đặc tả API trong `docs/api/wishlist.md`.

- Trạng thái code hiện tại: **236/236 Backend integration tests** và **98/98 Frontend tests** pass (23 test files); .NET build, React production build và oxlint đạt chuẩn (0 warnings, 0 errors).
- Triển khai hoàn thiện T36: Đánh giá & nhận xét sản phẩm (Reviews & Ratings) theo kế hoạch [2026-11-04-product-reviews-ratings.md](superpowers/plans/2026-11-04-product-reviews-ratings.md):
  1. Migration `008_product_reviews.sql` với bảng `store.product_reviews`, foreign keys, check constraints và partial unique index `ux_product_reviews_active_customer_product` (`WHERE deleted_at IS NULL`).
  2. EF Core model mapping (`ProductReview.cs`, `StoreDbContext`), test fixture `ProductReviewsSchema.sql` và compose mount.
  3. Service backend `PostgresProductReviewService` đáp ứng Verified Purchase Guard (`COMPLETED` + `DELIVERED`), concurrency lock an toàn (`user_accounts FOR NO KEY UPDATE` + khóa các `sales_orders` đủ điều kiện + bắt PostgreSQL 23505), che tên reviewer chống rò rỉ PII, source-of-truth SQL aggregation không counter lệch, owner-only update/soft-delete và kiểm duyệt Staff/Admin với Audit Log transactional. Bài test cạnh tranh Review ↔ Full Refund xác nhận không deadlock và không để lại review công khai khi khách đã hoàn toàn bộ sản phẩm.
  4. 3 Controllers (`CatalogReviewsController`, `CustomerReviewsController`, `ReviewModerationController`), `ProductReviewExceptionHandler` chuẩn RFC 7807, mở rộng DTO catalog/orders (`AverageRating`, `ReviewCount`, `ProductId`).
  5. 29 integration tests backend (`ProductReviewTests.cs`) kiểm chứng eligibility, owner-only, masking, moderation, lọc sao/phân trang, public visibility, snapshot nhất quán khi moderation xen giữa truy vấn, update/delete cạnh tranh moderation và validation query.
  6. Frontend React: feature API `reviews.api.ts`, bộ components (`RatingStars`, `RatingSummary`, `ReviewCard`, `ReviewForm`, `ReviewList`, `ReviewModerationPanel`), nhúng ProductCard, PDP, Orders page và Back Office tabs.
  7. 8 nhóm unit/component frontend tests (`reviews.test.tsx`) pass 100% cùng Playwright E2E spec `04-product-reviews.spec.ts`.
- Triển khai hoàn thiện T35: E2E Testing & Release Gate với Playwright, Mailpit integration, Demo Seed idempotency, GitHub Actions CI workflow, security headers middleware, Nginx CSP và bộ tài liệu vận hành release. Toàn bộ kịch bản Playwright E2E chạy thực tế trên Docker demo stack đã đạt 100% pass:
  1. Kiểm tra subject email reset không phân biệt hoa/thường (`/đặt lại mật khẩu/i`).
  2. Đồng bộ selector form mật khẩu mới (`input[name="newPassword"]`, `input[name="confirmPassword"]`, nút "Lưu mật khẩu mới").
  3. Định danh chính xác trường số điện thoại địa chỉ qua `#addrPhone`.
  4. Đồng bộ tiêu đề trang đơn hàng khớp UI: "Đơn hàng của tôi".
  5. Đồng bộ CTA đăng nhập sau xác minh email qua `a[href="/login?verified=1"]` và chờ redirect `/` sau khi Staff đăng nhập.
- Triển khai hoàn thiện T33: Password reset và email verification với outbox bền vững, token bảo mật không lưu thô, anti-enumeration tuyệt đối, Mailpit trong Docker Compose, và bộ test concurrency/security toàn diện. Đã chuẩn hóa thứ tự khóa User -> Token đồng nhất loại bỏ rủi ro deadlock; ngăn Staff ở trạng thái INVITATION_PENDING và Customer chưa xác minh email bypass luồng xác thực; hỗ trợ tài khoản Google-only thiết lập mật khẩu lần đầu an toàn; kiểm tra EmailSnapshot dưới khóa độc quyền chống rò rỉ hoặc tráo đổi token.
- Triển khai hoàn thiện T34: Quản trị tài khoản nội bộ và Staff với migration 006 idempotent, service kiểm soát khóa nghiêm ngặt (User -> Token), transaction audit logs, transactional email outbox, workspace quản trị tài khoản Admin Dashboard và trang kích hoạt Staff. Phục hồi và chứng minh không deadlock giữa Resend và Accept dưới concurrency.
- T31 giữ nguyên cho Customer Order Center; Google Sign-In được chuẩn hóa thành T32 để không còn trùng ID. T33 dành riêng cho password recovery và email verification. T34 dành cho quản trị tài khoản nội bộ và nhân sự. T35 dành cho E2E Testing & Release Gate. T36 Reviews, T37 Wishlist và T38 Vouchers đã hoàn tất. T19–T22 PayOS đã hoàn tất runtime implementation; việc bật production vẫn bị chặn bởi sandbox/provider release gate theo ADR 0003 và runbook PayOS.
