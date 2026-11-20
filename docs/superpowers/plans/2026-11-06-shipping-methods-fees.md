# T48 — Shipping Methods & Fees

Ngày lập: 06/11/2026  
Trạng thái: **Kế hoạch đã chốt — chưa triển khai**

## 1. Mục tiêu

Thay thế giả định “phí giao hàng bằng 0” bằng module vận chuyển nội bộ có thể vận hành thật:

1. Admin cấu hình nhiều phương thức giao hàng và phụ phí theo tỉnh/thành.
2. Customer nhận báo giá từ server, chọn phương thức tại Cart và xác nhận rõ phí/ETA trước checkout.
3. Checkout khóa và tính lại phí, chống dùng báo giá cũ hoặc payload client giả mạo.
4. Đơn hàng lưu snapshot phương thức, phí và ETA; mọi màn hình COD/Fulfillment/Customer Order dùng snapshot này.
5. Bảo toàn idempotency, voucher/Flash Sale, COD collection, reporting và returns dưới concurrency.

T48 không tích hợp API hãng vận chuyển. Tracking number, tạo vận đơn, webhook carrier và đối soát cước thực tế được tách sang task sau.

## 2. Quyết định nghiệp vụ

### 2.1. Công thức tiền

```text
merchandise_subtotal = tổng quantity × unit_price sau Flash Sale
voucher_discount     = giảm trên merchandise_subtotal
shipping_fee         = phí phương thức + phụ phí tỉnh, hoặc 0 nếu đạt ngưỡng miễn phí
order_total          = merchandise_subtotal - voucher_discount + shipping_fee
```

- Voucher T38 không giảm shipping fee trong T48.
- Ngưỡng miễn phí được xét trên `merchandise_subtotal` **trước voucher**, tránh voucher làm mất quyền miễn phí.
- Tất cả tiền là số nguyên VND lưu `numeric(18,2)` và phải bằng `floor(value)`.
- `shipping_fee >= 0`; tổng đơn phải `> 0`.

### 2.2. Chính sách hoàn tiền

- T39 tiếp tục hoàn theo item net sau voucher; shipping fee mặc định **không hoàn**.
- Trả toàn bộ item có thể làm Payment ở `PARTIALLY_REFUNDED` vì phí giao hàng đã được thực hiện.
- Hoàn shipping do lỗi vận chuyển là nghiệp vụ riêng, không suy diễn tự động trong T48.

### 2.3. Báo giá và chống surprise charge

- Quote chỉ phục vụ UX, không giữ giá và không tạo reservation.
- Quote chứa `shippingMethodId`, `methodRevision`, `shippingFee`.
- Checkout nhận lại cả ba giá trị, khóa method rồi tính lại từ source of truth.
- Nếu revision hoặc fee khác quote, trả `409 SHIPPING_QUOTE_CHANGED`; frontend tải quote mới và yêu cầu Customer xác nhận lại.
- Không bao giờ âm thầm tăng/giảm phí trong một checkout request đã được người dùng xác nhận.

### 2.4. Vòng đời phương thức

```text
INACTIVE -> ACTIVE -> INACTIVE
```

- Tạo mới ở `INACTIVE`.
- Chỉ `ACTIVE` được quote/checkout.
- Không hard-delete phương thức đã tồn tại; lịch sử đơn dựa vào snapshot và FK nullable cho legacy.
- Có đúng tối đa một method `is_default=true` đang active.

## 3. Phạm vi

### 3.1. Có trong T48

- Phương thức Standard/Express do Admin quản trị.
- Base fee, ngưỡng miễn phí, ETA min/max ngày.
- Phụ phí tùy chọn theo `province_key`.
- Quote theo địa chỉ đã lưu và explicit checkout lines.
- Snapshot vào SalesOrder và truyền xuyên suốt Order/COD/Fulfillment/Reporting.
- Admin UI, Customer Cart UI, tests và Playwright Flow 14.

### 3.2. Không có trong T48

- API GHN/GHTK/Viettel Post hoặc mua nhãn vận chuyển.
- Tracking URL/webhook carrier.
- Khối lượng/kích thước kiện, volumetric weight.
- Nhiều kho xuất hàng hoặc split shipment.
- Shipping voucher, COD surcharge, bảo hiểm hàng hóa.
- Đổi phương thức sau khi Order đã tạo.
- Hoàn phí ship tự động khi return/refund.

## 4. Migration `018_shipping_methods_fees.sql`

### 4.1. `store.shipping_methods`

Các cột:

- `id uuid PK`
- `code varchar(32) UNIQUE`, format `^[A-Z0-9_-]{3,32}$`
- `name varchar(120)`
- `description varchar(500) NULL`
- `method_status varchar(20)` — `ACTIVE|INACTIVE`
- `base_fee numeric(18,2) >= 0`
- `free_shipping_threshold numeric(18,2) NULL` và `> 0` nếu có
- `estimated_min_days smallint >= 0`
- `estimated_max_days smallint >= estimated_min_days`
- `is_default boolean`
- `display_order integer >= 0`
- `revision bigint >= 1`
- `created_by`, `updated_by` FK UserAccount
- timestamps.

Indexes:

- Unique code không phân biệt hoa/thường bằng normalized uppercase code.
- Partial unique `is_default` khi `method_status='ACTIVE'`.
- Queue/list `(method_status, display_order, id)`.

### 4.2. `store.shipping_method_province_rates`

- `id uuid PK`
- `shipping_method_id` FK `ON DELETE RESTRICT`
- `province_key varchar(160)` đã normalize
- `surcharge_fee numeric(18,2) >= 0`
- `is_active boolean`
- timestamps + actor.
- Unique `(shipping_method_id, province_key)`.

Normalization V1: Unicode NFKC, trim, collapse whitespace, uppercase invariant; giữ nguyên dấu tiếng Việt. Nếu không có override, dùng base fee.

### 4.3. Snapshot trên `store.sales_orders`

Thêm:

- `shipping_method_id uuid NULL` — nullable cho đơn legacy, FK `RESTRICT`.
- `shipping_method_code_snapshot varchar(32) NOT NULL`
- `shipping_method_name_snapshot varchar(120) NOT NULL`
- `shipping_fee numeric(18,2) NOT NULL DEFAULT 0`
- `shipping_estimated_min_days smallint NULL`
- `shipping_estimated_max_days smallint NULL`
- `shipping_destination_province_key varchar(160) NULL`
- `shipping_method_revision bigint NULL`.

Backfill đơn cũ:

```text
code = LEGACY_FREE
name = Miễn phí (đơn lịch sử)
shipping_fee = 0
method_id/revision/ETA = null
```

Drop/recreate `ck_sales_orders_subtotal_discount` thành:

```text
total_amount = subtotal_amount - voucher_discount_amount + shipping_fee
```

Migration phải idempotent, chạy được từ DB trống, volume legacy và khi reapply.

## 5. Application contracts

### 5.1. Quote

```csharp
ShippingQuoteRequest(
  Guid ShippingAddressId,
  IReadOnlyList<ShippingQuoteLineRequest> Lines)

ShippingQuoteDto(
  Guid ShippingMethodId,
  string Code,
  string Name,
  string? Description,
  decimal ShippingFee,
  decimal BaseFee,
  decimal ProvinceSurcharge,
  bool FreeShippingApplied,
  decimal? FreeShippingThreshold,
  short EstimatedMinDays,
  short EstimatedMaxDays,
  long MethodRevision,
  bool IsDefault)
```

Line giữ `productSkuId`, `quantity`, `flashSaleOfferId` như Checkout để server tự tính merchandise subtotal; không nhận subtotal từ client.

### 5.2. Checkout v3

Mở rộng `CreateCheckoutRequest`:

- `shippingMethodId` required.
- `shippingMethodRevision >= 1`.
- `quotedShippingFee >= 0`.

Mở rộng `CheckoutResultDto`:

- `shippingFee`
- `shippingMethod` snapshot gồm code/name/ETA.

Canonical payload chuyển sang `checkout-v3` và chứa method ID, revision, quoted fee. Replay key cũ đã `SUCCEEDED/REJECTED` vẫn trả snapshot cũ; request mới không được dùng v1/v2 fingerprint.

### 5.3. Admin

- DTO list/detail method và province rate.
- Create/update request validation.
- Update chứa `expectedRevision`; mismatch trả `409 SHIPPING_METHOD_STALE`.
- Status/default transitions là action rõ ràng, không cho client patch tùy ý.

## 6. API

### 6.1. Customer

| Method | Endpoint | Mục đích |
|---|---|---|
| POST | `/api/v1/shipping/quotes` | Quote toàn bộ method active cho address + lines |

- Policy `Customer`, CSRF bắt buộc vì request chứa dữ liệu tài khoản và có rate-limit.
- Address phải owner-only; không tồn tại/không thuộc Customer đều trả 404.
- Quote không mutate DB.

### 6.2. Admin

| Method | Endpoint | Mục đích |
|---|---|---|
| GET | `/api/v1/admin/shipping-methods` | Danh sách method |
| GET | `/api/v1/admin/shipping-methods/{id}` | Chi tiết + province rates |
| POST | `/api/v1/admin/shipping-methods` | Tạo INACTIVE |
| PATCH | `/api/v1/admin/shipping-methods/{id}` | Cập nhật config theo expectedRevision |
| POST | `/api/v1/admin/shipping-methods/{id}/activate` | Kích hoạt |
| POST | `/api/v1/admin/shipping-methods/{id}/deactivate` | Ngừng quote/checkout mới |
| POST | `/api/v1/admin/shipping-methods/{id}/make-default` | Đặt mặc định nguyên tử |
| PUT | `/api/v1/admin/shipping-methods/{id}/province-rates/{provinceKey}` | Upsert phụ phí |
| DELETE | `/api/v1/admin/shipping-methods/{id}/province-rates/{provinceKey}` | Bỏ override |

Mọi mutation ghi Audit Log transactional: `SHIPPING_METHOD_CREATED`, `SHIPPING_METHOD_UPDATED`, `SHIPPING_METHOD_ACTIVATED`, `SHIPPING_METHOD_DEACTIVATED`, `SHIPPING_METHOD_DEFAULT_CHANGED`, `SHIPPING_PROVINCE_RATE_UPSERTED`, `SHIPPING_PROVINCE_RATE_REMOVED`.

## 7. Pricing service và lock order

Tạo `IShippingQuoteService`/`PostgresShippingQuoteService` dùng chung calculator thuần:

```text
feeBeforeFree = baseFee + activeProvinceSurcharge
shippingFee = subtotal >= freeThreshold ? 0 : feeBeforeFree
```

Quote read-side dùng `AsNoTracking`, deterministic sorting:

1. default trước,
2. `display_order`,
3. fee,
4. ID.

Checkout lock order mới:

```text
Customer
→ ShippingMethod
→ Voucher
→ Product
→ FlashSale Campaign/Offer
→ SKU
→ Inventory
```

- Checkout khóa ShippingMethod bằng `FOR SHARE` trước voucher/product locks.
- Admin update/activate/rate mutation khóa parent ShippingMethod `FOR UPDATE` trước child rate.
- Vì Admin shipping không khóa commerce entities khác, không tạo chu trình ngược.
- Sau khi lấy mọi price-related lock, refresh `now`, tính lại merchandise subtotal, voucher và shipping.

## 8. Checkout invariants

1. Method phải active tại thời điểm checkout dưới lock.
2. Method revision và computed fee phải khớp quote client gửi.
3. Address province snapshot được normalize một lần và lưu cùng order.
4. Order, Payment `ExpectedAmount`, COD collected amount và PaymentTransaction đều dùng `order_total` đã gồm shipping.
5. Nếu checkout rollback/reject vì shipping, không giữ inventory, Flash Sale quota hoặc voucher budget/usage.
6. Idempotency replay trả nguyên shipping snapshot ban đầu dù Admin đã đổi phí sau đó.
7. Admin deactivate/update cạnh checkout chỉ có outcome tuyến tính hóa: checkout theo config cũ đã khóa trước, hoặc bị reject/quote-changed theo config mới.

## 9. Tích hợp module hiện tại

### 9.1. Customer Order / COD / Fulfillment

Mở rộng DTO để trả:

- method code/name,
- shipping fee,
- ETA snapshot.

Staff thấy “COD phải thu” đã gồm phí ship. Giao lại không tính thêm phí.

### 9.2. Voucher

- Preview voucher vẫn dùng merchandise subtotal và không cộng shipping.
- Cart hiển thị tách: tạm tính, voucher, phí vận chuyển, tổng thanh toán.
- Thay đổi voucher không đổi shipping fee/free-threshold nhưng vẫn tính lại tổng UI.

### 9.3. Returns

- Refund calculator tiếp tục phân bổ voucher theo item gross, không phân bổ shipping fee.
- API eligibility/detail hiển thị shipping fee để giải thích chênh lệch giữa item refund và order total.
- Thêm regression test full-item return không hoàn shipping.

### 9.4. Reporting

`RevenueReportDto` thêm `ShippingRevenue` để tách doanh thu shipping đã thu. Gross/Net hiện tại vẫn dựa trên Payment expected amount nên bao gồm shipping; không được cộng shipping lần hai.

## 10. Frontend

### 10.1. Cart/Checkout

- Sau khi có Customer + address + cart lines, gọi quote API.
- Card radio cho từng method: tên, mô tả, phí, ETA, badge mặc định/miễn phí.
- Auto-select method default; nếu không có thì method đầu tiên theo server order.
- Đổi address/cart/offer làm quote cũ invalid và clear checkout intent.
- Đổi method cũng clear `hcc.checkout-intent.v1` trước khi tạo fingerprint v3.
- Khi `SHIPPING_QUOTE_CHANGED`, reload quote, giữ Cart và hiện cảnh báo cần xác nhận lại.
- Skeleton, empty state “Chưa có phương thức giao hàng”, retry và mobile layout đầy đủ.

### 10.2. Checkout success và Orders

Hiển thị rõ:

```text
Tạm tính hàng
- Voucher
+ Phí vận chuyển
= Tổng COD
Phương thức / ETA
```

### 10.3. Admin Dashboard

Tab “Vận chuyển & Phí”:

- danh sách active/inactive và default,
- form tạo/sửa base fee, threshold, ETA, thứ tự,
- activate/deactivate/make-default có ConfirmDialog,
- bảng phụ phí tỉnh với upsert/remove,
- stale revision 409 tự reload thay vì ghi đè.

## 11. Error contract

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `SHIPPING_INVALID_ARGUMENT` | Payload/range/province key sai |
| 404 | `SHIPPING_ADDRESS_NOT_FOUND` | Address không thuộc Customer |
| 404 | `SHIPPING_METHOD_NOT_FOUND` | Method không tồn tại |
| 409 | `SHIPPING_METHOD_NOT_ACTIVE` | Method đã bị deactivate |
| 409 | `SHIPPING_METHOD_STALE` | Admin expectedRevision cũ |
| 409 | `SHIPPING_QUOTE_CHANGED` | Revision hoặc fee không còn khớp |
| 409 | `SHIPPING_DEFAULT_REQUIRED` | Không thể làm hệ thống mất default active method |
| 409 | `SHIPPING_CODE_CONFLICT` | Code trùng, kể cả race 23505 |
| 409 | `SHIPPING_PROVINCE_RATE_CONFLICT` | Upsert/race không thể resolve |

ProblemDetails luôn có `correlationId`; không trả raw SQL/storage details.

## 12. Kiểm thử

### 12.1. Migration

1. DB trống áp migration 001–018.
2. Reapply 018 không nhân dữ liệu/constraint.
3. Legacy orders backfill `LEGACY_FREE`, fee 0 và vẫn hợp lệ.
4. New total constraint chặn sai công thức hoặc số lẻ VND.

### 12.2. Backend integration

1. RBAC Guest 401, Customer/Admin separation 403.
2. Address owner isolation.
3. Base fee, surcharge và fallback province.
4. Free threshold boundary: `<`, `=`, `>`.
5. Flash Sale subtotal dùng giá sale; voucher không làm mất free shipping.
6. Checkout snapshot và Payment expected amount gồm shipping.
7. Replay idempotency giữ snapshot cũ.
8. Quote changed trả 409 và zero inventory/voucher/quota leak.
9. Concurrent Admin update/deactivate ↔ checkout có outcome tuyến tính, không 500/deadlock.
10. Concurrent duplicate code trả một success + một 409.
11. Default method invariant dưới concurrent make-default.
12. Full return không hoàn shipping; reporting tách shipping revenue đúng.

### 12.3. Frontend

- Runtime parser từ chối malformed quote.
- Chọn default/method khác và payload checkout v3 đúng.
- Address/cart change reload quote, clear intent.
- Free shipping/ETA/summary responsive.
- 409 refresh quote và không tự retry checkout.
- Admin CRUD/status/default/rate/stale handling.

### 12.4. Playwright Flow 14

1. Admin tạo Standard + Express, đặt Standard default và thêm phụ phí tỉnh.
2. Customer chọn address, xem hai quote và chọn Express.
3. Checkout COD; success page và Order detail có đúng snapshot/fee.
4. Admin đổi phí sau checkout; order cũ không đổi.
5. Tạo quote mới rồi Admin sửa phí trước submit; checkout trả quote-changed, không giữ tồn.
6. Customer xác nhận quote mới và checkout thành công.
7. Staff fulfillment/thu COD đúng tổng gồm phí ship.

## 13. Chia task triển khai

| Bước | Hạng mục | Acceptance |
|---|---|---|
| T48.1 | ADR 0013 + migration 018 | Schema/backfill/reapply/legacy tests pass |
| T48.2 | Shipping calculator + quote service | Boundary/free/surcharge deterministic tests pass |
| T48.3 | Admin management API | RBAC, audit, revision/default invariants pass |
| T48.4 | Customer quote API | Owner-only, Flash Sale pricing, stable ordering pass |
| T48.5 | Checkout v3 integration | Snapshot/idempotency/zero-leak/concurrency pass |
| T48.6 | Order/COD/Fulfillment/Return/Reporting | Shipping data và money semantics đồng nhất |
| T48.7 | Customer Cart/Order UI | Quote selection, summary, stale handling responsive |
| T48.8 | Admin Shipping workspace | Method/rate/status/default workflow hoàn chỉnh |
| T48.9 | Playwright Flow 14 + docs | Docker E2E và release gate xanh |

## 14. Release gate

Chỉ đóng T48 khi:

- Full backend suite pass trên PostgreSQL thật.
- Full frontend suite, oxlint và production build pass.
- Migration 018 pass DB trống, legacy upgrade và reapply.
- Concurrency Admin update/deactivate/default ↔ checkout không deadlock/500.
- Checkout reject không rò inventory, Flash Sale hoặc voucher counters.
- Existing Playwright flows 3, 6, 7, 11–13 không regression.
- Playwright Flow 14 pass trên Docker demo stack.
- `docs/api/shipping.md`, README, TASKS, ADR 0013 và Graphify được cập nhật.

## 15. Thứ tự bắt đầu

Triển khai theo vertical slice an toàn:

1. T48.1 migration/model + migration tests.
2. T48.2 calculator thuần và quote projection.
3. T48.3–T48.4 APIs.
4. T48.5 checkout v3 và concurrency trước khi làm UI.
5. T48.6 downstream DTO/report/refund regression.
6. T48.7–T48.8 frontend.
7. T48.9 E2E/release/docs/Graphify.
