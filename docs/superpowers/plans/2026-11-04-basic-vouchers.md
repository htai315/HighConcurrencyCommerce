# Implementation Plan — T38: Basic Vouchers

Ngày chốt: 2026-11-04  
Trạng thái: **Sẵn sàng triển khai**  
Phạm vi: voucher cấp đơn hàng, quản trị bởi Admin, áp dụng trong checkout COD hiện có. PayOS tiếp tục deferred theo ADR 0003.

## 1. Mục tiêu và phạm vi

T38 bổ sung mã giảm giá cơ bản nhưng phải giữ nguyên các invariant concurrency của Cart, Checkout, Inventory, Flash Sale, COD và Fulfillment:

- Voucher giảm số tiền cố định hoặc phần trăm có mức giảm tối đa.
- Điều kiện subtotal tối thiểu, thời gian hiệu lực, giới hạn tổng lượt, giới hạn mỗi Customer và ngân sách tổng.
- Một đơn chỉ dùng tối đa một voucher; mã không tự gắn vào cart và không lưu trong localStorage.
- Preview trên giỏ hàng chỉ mang tính tham khảo, không giữ lượt hoặc ngân sách.
- Checkout mới là điểm allocation nguyên tử: voucher, tồn kho, Flash Sale quota và Order cùng commit hoặc cùng rollback.
- Admin quản trị draft/activate/pause/resume; mọi thay đổi tài chính có Audit Log.
- Customer thấy subtotal, voucher discount và tổng phải trả trong checkout, Order Center và kết quả đặt hàng.

Ngoài phạm vi T38: voucher theo category/SKU, voucher cá nhân, referral, miễn phí vận chuyển, campaign stacking nhiều voucher, tự động chọn voucher tốt nhất, PayOS, refund tiền thật và notification marketing.

## 2. Quyết định nghiệp vụ đã chốt

### 2.1 Voucher và cách tính tiền

1. Code được trim + uppercase, dài 3–32 ký tự, chỉ `[A-Z0-9_-]`; unique case-insensitive và không tái sử dụng code cũ.
2. `FIXED_AMOUNT`: `discount_value > 0`, `max_discount_amount = null`.
3. `PERCENTAGE`: phần trăm là số nguyên 1–99 và bắt buộc có `max_discount_amount > 0`.
4. Tất cả số tiền voucher là số nguyên VND dù DB dùng `numeric(18,2)`.
5. `eligible_subtotal` là tổng giá SKU sau khi áp Flash Sale hợp lệ, trước voucher; hiện chưa có shipping fee.
6. Điều kiện đơn tối thiểu được đánh giá trên `eligible_subtotal` sau Flash Sale.
7. Phần trăm dùng `floor(subtotal * percent / 100)`, sau đó cap bởi `max_discount_amount`.
8. Discount cuối phải `> 0` và `< eligible_subtotal`; không cho tạo đơn tổng tiền 0 vì `payment_transactions.amount` bắt buộc dương.
9. Một Order tối đa một voucher. Voucher được phép dùng cùng Flash Sale vì voucher chỉ giảm subtotal sau Flash Sale.
10. Preview không bảo đảm quyền sử dụng. Checkout luôn tính lại giá, hiệu lực, giới hạn và ngân sách dưới row lock.

### 2.2 Lifecycle Voucher

- `DRAFT`: được sửa toàn bộ cấu hình, chưa dùng được.
- `ACTIVE`: có thể dùng khi `starts_at <= now < ends_at`.
- `PAUSED`: không nhận allocation mới; allocation đã HELD vẫn được giữ nguyên.
- `ENDED`: terminal cho vận hành; không nhận allocation mới.
- Thời gian bắt đầu trong tương lai không tạo status riêng; Voucher `ACTIVE` nhưng chưa tới `starts_at` vẫn chưa effective.
- Sau lần activate đầu tiên, code và các trường kinh tế trở thành immutable. Admin chỉ pause/resume; không được sửa quyền lợi của các Order đang giữ voucher.

Activate yêu cầu đầy đủ invariant: thời gian hợp lệ, budget dương, total usage dương, per-customer limit không vượt total limit, cấu hình discount đúng loại. Resume không được phép nếu đã hết thời gian hoặc counters/budget đã cạn.

### 2.3 Lifecycle allocation

`voucher_redemptions.redemption_status`:

- `HELD`: tạo cùng transaction checkout; chiếm một lượt và `discount_amount` trong budget.
- `CONSUMED`: chỉ chuyển từ HELD khi Order `COMPLETED` và COD collection được ghi nhận thành công.
- `RELEASED`: Order hủy trước xác nhận, COD expiry hoặc giao thất bại đã hoàn kho.

Các quy tắc:

- `HELD -> CONSUMED|RELEASED` là terminal và idempotent.
- COD confirm chỉ xác nhận Order/Inventory; voucher vẫn HELD.
- Voucher pause/end/expiry sau checkout không làm mất allocation HELD đã commit.
- Delivery failed nhưng chưa hoàn kho vẫn giữ HELD. Chỉ release khi `ReturnStockAsync` commit thành công.
- Per-customer limit và global limit tính cả `HELD + CONSUMED`; `RELEASED` không tính.
- Budget khả dụng = `budget_total - budget_held - budget_consumed`.

## 3. ADR và lock order

Tạo `docs/adr/0005-voucher-allocation-and-release.md` để cố định:

```text
Checkout:
Customer FOR NO KEY UPDATE
  → Voucher FOR UPDATE (nếu có)
  → FlashSaleCampaign FOR UPDATE theo UUID
  → FlashSaleOffer FOR UPDATE theo UUID
  → ProductSku FOR UPDATE theo UUID
  → InventoryBalance FOR UPDATE theo SKU UUID

Order terminal transitions:
SalesOrder FOR UPDATE
  → Voucher FOR UPDATE (nếu Order có redemption)
  → FlashSaleCampaign / Offer
  → ProductSku / InventoryBalance
```

- Tất cả luồng Customer cancel, COD expiry, COD complete và delivery return phải dùng cùng chiều `Order -> Voucher -> Flash Sale -> SKU`.
- Admin mutation chỉ khóa Voucher, không khóa Order/Customer, nên không tạo chu trình ABBA.
- Hai Customer tranh slot/budget cuối cùng bị tuần tự hóa trên cùng Voucher row.
- Checkout không voucher giữ nguyên lock order hiện tại.
- Lock timeout/deadlock tiếp tục tuân thủ ADR 0002; transient failure rollback toàn bộ, không ghi `REJECTED` sai.

## 4. Database — migration 010

Tạo additive migration `database/010_basic_vouchers.sql`, mount trong Compose và embed vào IntegrationTests fixture.

### 4.1 `store.vouchers`

Các cột chính:

```text
id, created_at, updated_at
code varchar(32)
voucher_status DRAFT|ACTIVE|PAUSED|ENDED
discount_type FIXED_AMOUNT|PERCENTAGE
discount_value numeric(18,2)
max_discount_amount numeric(18,2) nullable
minimum_order_amount numeric(18,2)
total_usage_limit integer
per_customer_limit integer
usage_held integer default 0
usage_consumed integer default 0
budget_total numeric(18,2)
budget_held numeric(18,2) default 0
budget_consumed numeric(18,2) default 0
starts_at, ends_at
activated_at nullable
created_by, activated_by nullable
revision integer default 0
```

DB constraints:

- unique index `upper(code)`.
- tiền và counters không âm; `starts_at < ends_at`.
- `usage_held + usage_consumed <= total_usage_limit`.
- `budget_held + budget_consumed <= budget_total`.
- `1 <= per_customer_limit <= total_usage_limit`.
- CHECK phân biệt FIXED/PERCENTAGE và percent 1–99.

### 4.2 `store.voucher_redemptions`

```text
id, voucher_id, customer_id, order_id
subtotal_snapshot, discount_amount
redemption_status HELD|CONSUMED|RELEASED
held_at, consumed_at nullable, released_at nullable
release_reason nullable
created_at, updated_at
```

- `order_id UNIQUE` bảo đảm một voucher mỗi Order.
- Unique `(voucher_id, order_id)` và index `(voucher_id, customer_id, redemption_status)`.
- FK dùng default NO ACTION để không xóa ledger tài chính.
- CHECK timestamp phù hợp status và `0 < discount_amount < subtotal_snapshot`.

### 4.3 Snapshot trên Order

Thêm vào `sales_orders`:

- `subtotal_amount numeric(18,2) NOT NULL` — backfill bằng `total_amount`.
- `voucher_discount_amount numeric(18,2) NOT NULL DEFAULT 0`.
- `voucher_code_snapshot varchar(32) NULL`.
- CHECK `total_amount = subtotal_amount - voucher_discount_amount`, `total_amount > 0`.

Migration phải reapply an toàn, giữ dữ liệu và timestamp. Với volume PostgreSQL đã tồn tại, tài liệu phải cung cấp lệnh apply 010 riêng; Docker init scripts không tự nâng cấp volume cũ.

## 5. Application contracts và API

### 5.1 Admin — `/api/v1/admin/vouchers`

Policy `Admin`, CSRF cho mutation, `Cache-Control: no-store`:

| Method | Endpoint | Mục đích |
|---|---|---|
| GET | `/admin/vouchers?page=&pageSize=&status=&q=` | Danh sách, counters và ngân sách |
| GET | `/admin/vouchers/{id}` | Chi tiết cấu hình + aggregate |
| POST | `/admin/vouchers` | Tạo DRAFT |
| PATCH | `/admin/vouchers/{id}` | Sửa khi DRAFT |
| POST | `/admin/vouchers/{id}/activate` | Validate và activate lần đầu |
| POST | `/admin/vouchers/{id}/pause` | Dừng allocation mới |
| POST | `/admin/vouchers/{id}/resume` | Mở lại nếu còn hiệu lực/quota/budget |
| POST | `/admin/vouchers/{id}/end` | Kết thúc vĩnh viễn |

Audit actions: `VOUCHER_CREATED`, `VOUCHER_UPDATED`, `VOUCHER_ACTIVATED`, `VOUCHER_PAUSED`, `VOUCHER_RESUMED`, `VOUCHER_ENDED`. Before/after data không chứa secret hoặc dữ liệu Customer.

### 5.2 Customer preview

`POST /api/v1/vouchers/preview` — Customer + CSRF:

```json
{ "code": "WELCOME10" }
```

Backend đọc Customer cart hiện tại, tính lại effective price/Flash Sale và trả:

```json
{
  "code": "WELCOME10",
  "subtotalAmount": 1500000,
  "discountAmount": 100000,
  "totalAmount": 1400000,
  "validUntil": "..."
}
```

Preview không insert redemption, không tăng counter và không hứa giữ quota. Cart trống hoặc không hợp lệ bị từ chối. Mọi thay đổi quantity/SKU/Flash Sale phải xóa preview UI hiện tại.

### 5.3 Checkout và Order DTO

Mở rộng `CreateCheckoutRequest`:

```json
{ "voucherCode": "WELCOME10", "shippingAddressId": "...", "paymentMethod": "COD", "lines": [] }
```

Mở rộng CheckoutResult, CustomerOrderDto, COD/fulfillment DTO cần thiết:

```text
subtotalAmount
voucherDiscountAmount
voucherCode (nullable)
totalAmount (số phải trả cuối)
```

Frontend parser phải backward-compatible: response/snapshot cũ không có trường voucher được hiểu là `subtotal = total`, `discount = 0`, `voucherCode = null`.

## 6. Checkout idempotency và pricing

### 6.1 Canonical payload

- Không voucher: tiếp tục hash chính xác `checkout-v1` hiện tại để replay CheckoutRequest lịch sử không bị vỡ.
- Có voucher: dùng `checkout-v2`, thêm `voucherCode` đã normalize uppercase.
- Cùng key, cùng code khác casing/whitespace là cùng intent.
- Cùng key nhưng thêm/xóa/đổi voucher trả `409 IDEMPOTENCY_KEY_REUSED`.
- Frontend fingerprint/sessionStorage phải bao gồm normalized voucher code; đổi voucher gọi `clearCheckoutIntent()`.

### 6.2 Atomic checkout

Trong savepoint `checkout_work`:

1. Khóa Customer và xử lý CheckoutRequest replay như hiện tại.
2. Nếu có code, khóa Voucher theo normalized code.
3. Khóa Campaign/Offer/SKU/Inventory theo order chuẩn.
4. Refresh `now` sau khi lấy toàn bộ lock.
5. Tính subtotal server-side từ giá SKU/Flash Sale.
6. Revalidate voucher status/time/minimum/counters/per-customer/budget.
7. Tính discount; bảo đảm tổng cuối dương.
8. Tạo Order snapshots, Payment expected amount cuối, voucher redemption HELD.
9. Tăng `usage_held` và `budget_held` trong cùng transaction.
10. Tạo inventory/Flash Sale allocation và CheckoutRequest success như hiện tại.

Bất kỳ lỗi nào rollback về savepoint, không để counter/redemption/order/tồn kho một phần. Business rejection được snapshot trong CheckoutRequest; muốn thử lại sau khi voucher có quota phải dùng idempotency key mới theo ADR 0002.

Error codes ổn định:

- `404 VOUCHER_NOT_FOUND`
- `409 VOUCHER_NOT_ACTIVE`, `VOUCHER_NOT_STARTED`, `VOUCHER_EXPIRED`
- `409 VOUCHER_MINIMUM_NOT_MET`
- `409 VOUCHER_USAGE_LIMIT_REACHED`, `VOUCHER_CUSTOMER_LIMIT_REACHED`
- `409 VOUCHER_BUDGET_EXHAUSTED`, `VOUCHER_DISCOUNT_INVALID`
- Admin: `VALIDATION_ERROR`, `INVALID_STATE`, `CONFLICT`, `NOT_FOUND`.

## 7. Terminal order integration

Tạo helper/service dùng chung để tránh ba nơi tự cập nhật counters khác nhau:

- `HoldAsync` chỉ gọi trong Checkout transaction.
- `ConsumeForCompletedOrderAsync` yêu cầu Order đang hoàn tất, redemption HELD; chuyển held counters/budget sang consumed.
- `ReleaseForClosedOrderAsync` chuyển HELD sang RELEASED và trừ held counters/budget.
- Nếu Order không có voucher: no-op.
- Nếu redemption đã ở đúng terminal state: idempotent no-op.
- Nếu state chéo bất hợp lệ: trả invariant error và rollback Order transition.

Điểm tích hợp:

| Luồng | Voucher transition |
|---|---|
| Customer cancel COD pending | HELD → RELEASED |
| COD expiry worker | HELD → RELEASED |
| Staff confirm COD | giữ HELD |
| Delivery completed + COD collected | HELD → CONSUMED |
| Delivery failed + return stock completed | HELD → RELEASED |

Audit hiện có của Order thêm voucher code/discount/state vào before/after snapshot. Reporting dùng `sales_orders.total_amount` cuối; bổ sung subtotal và discount vào export/summary khi phù hợp, không cộng lại item line totals để tính revenue.

## 8. Frontend React

### 8.1 Customer Cart

- Card nhập voucher trong phần tóm tắt: input code, “Áp dụng”, “Bỏ mã”.
- Chỉ Customer mới thấy; Guest được nhắc đăng nhập.
- Hiển thị subtotal, dòng giảm voucher và tổng thanh toán.
- Khi quantity/SKU/Flash Sale thay đổi: xóa preview và checkout idempotency intent.
- Preview có loading/error/success rõ ràng; không tự retry mutation.
- Checkout gửi normalized voucher code đã preview; server vẫn tính lại hoàn toàn.
- Với lỗi voucher xác định, clear checkout intent và giữ cart để Customer sửa/xóa code.
- Kết quả checkout và Order Center hiển thị code, subtotal, discount, total.

### 8.2 Admin Dashboard

- Tab “Mã giảm giá”: filter/search, budget progress, held/consumed usage.
- Form tạo/sửa DRAFT với conditional fields cho FIXED/PERCENTAGE.
- Activate/pause/resume/end dùng confirm dialog; disable action sai state.
- Hiển thị thời gian theo timezone UI nhưng gửi ISO UTC.
- Runtime parser strict; không tin dữ liệu API malformed.

## 9. Test matrix bắt buộc

### 9.1 Backend integration

Tối thiểu các nhóm:

1. RBAC/CSRF: Guest 401, Customer/Staff 403 trên Admin, Admin 403 trên Customer preview.
2. Admin validation: code normalize/unique, type-specific fields, time, immutable after activate, Audit Log.
3. Preview: fixed, percent floor/cap, minimum, paused/not-started/expired, không mutation counters.
4. Checkout fixed/percent và Flash Sale stacking; Order/Payment/response snapshots đúng.
5. Idempotency: replay cùng key không double hold; đổi voucher cùng key conflict; legacy no-voucher v1 replay vẫn đúng.
6. Hai Customer tranh global slot cuối: đúng một success, một rejection, không 500.
7. Hai Customer tranh phần budget cuối: không over-budget.
8. Một Customer checkout đồng thời bằng key khác: không vượt per-customer limit.
9. Checkout fail vì tồn kho/Flash Sale/address sau khi khóa Voucher: không rò counter/redemption.
10. Customer cancel và COD expiry release đúng một lần, voucher dùng lại được.
11. COD confirm không consume; delivery completed consume đúng một lần.
12. Delivery return stock release voucher HELD.
13. Pause/end cạnh tranh checkout: hoặc checkout hold trước, hoặc Admin transition trước và checkout bị chặn; không trạng thái nửa vời.
14. Migration 010 chạy lặp giữ dữ liệu/backfill chính xác.
15. Order/reporting DTO owner-only và không rò thông tin voucher nội bộ.

### 9.2 Frontend tests

- Apply/remove voucher, normalized code và CSRF.
- Preview fixed/percent, summary tiền đúng.
- Cart mutation làm invalid preview + clear idempotency intent.
- Checkout gửi voucher và hiển thị snapshot.
- Mapping tất cả stable errors; timeout giữ cùng checkout key.
- Admin conditional validation/lifecycle/actions/audit-facing state.
- Parser từ chối negative/NaN/invalid status; backward-compatible Order cũ.

### 9.3 Playwright E2E thật

1. Admin tạo + activate voucher trên demo stack.
2. Customer riêng áp voucher, checkout COD, kiểm tra Order snapshot và Customer khác không vượt limit/quota.
3. Staff confirm/fulfill/deliver; voucher chuyển CONSUMED và counters đúng.
4. Kịch bản COD pending cancel hoặc expiry: voucher được RELEASED và có thể sử dụng lại.

Không chỉ chạy test discovery; phải chạy Playwright trên Docker + PostgreSQL + Mailpit thật.

## 10. Chia task triển khai

| Task | Nội dung | Điều kiện hoàn thành |
|---|---|---|
| T38.1 | ADR 0005 + migration 010 + EF models/mapping/fixture/Compose | DB trống và migration reapply pass |
| T38.2 | Voucher contracts, Admin service/API, Audit Log | Lifecycle + RBAC tests pass |
| T38.3 | Customer preview service/API | Pricing/validation không mutation pass |
| T38.4 | Checkout v1/v2 compatibility + HELD allocation | Idempotency/concurrency/rollback tests pass |
| T38.5 | Cancel/expiry/complete/return lifecycle | Release/consume exactly-once tests pass |
| T38.6 | Order/reporting DTO snapshots | Tổng tiền và owner isolation pass |
| T38.7 | Cart/Order UI + Admin Voucher workspace | Unit tests, accessibility, responsive pass |
| T38.8 | Playwright E2E, API docs, README/TASKS, release gate | Toàn suite xanh, Graphify updated |

## 11. Thứ tự triển khai và release gate

Thứ tự bắt buộc: **T38.1 → T38.2 → T38.3 → T38.4 → T38.5 → T38.6 → T38.7 → T38.8**.

Không được đưa UI voucher vào trước khi schema và checkout allocation tests đạt. Không đánh dấu T38 hoàn thành chỉ dựa trên unit test hoặc Playwright discovery.

Release gate cuối:

```powershell
dotnet test HighConcurrencyCommerce/HighConcurrencyCommerce.IntegrationTests/HighConcurrencyCommerce.IntegrationTests.csproj
npm test --prefix frontend -- --run
npm run lint --prefix frontend
npm run build --prefix frontend
docker compose config -q
docker compose -f compose.yaml -f compose.demo.yaml up -d --build --wait
npm run test:e2e --prefix frontend
python -m graphify update .
```

Sau E2E phải dọn stack test biệt lập; không xóa volume phát triển của người dùng.
