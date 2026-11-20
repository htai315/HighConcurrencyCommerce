# ADR 0005 — Voucher Allocation, Release Lifecycle và Thứ tự Khóa Chống Deadlock

Trạng thái: **Đã chốt** ngày 04/11/2026. Phạm vi T38; là cơ sở kiến trúc cho phân bổ, giải phóng và quản trị vòng đời voucher.

---

## 1. Bối cảnh

Hệ thống High Concurrency Commerce đã triển khai hoàn thiện Cart, Checkout (ADR 0002), Inventory Allocation, Flash Sale (HELD + SOLD allocation), COD Order Lifecycle và Fulfillment.

Yêu cầu nghiệp vụ của T38 bổ sung **Mã giảm giá cơ bản (Basic Vouchers)**:
1. Voucher cấp đơn hàng (Order-level), hỗ trợ 2 loại: `FIXED_AMOUNT` (giảm số tiền cố định) hoặc `PERCENTAGE` (giảm theo % kèm trần `max_discount_amount`).
2. Điều kiện giá trị đơn tối thiểu (`minimum_order_amount`), thời gian hiệu lực (`starts_at`, `ends_at`), giới hạn tổng lượt dùng (`total_usage_limit`), giới hạn mỗi khách (`per_customer_limit`) và tổng ngân sách tối đa (`budget_total`).
3. Một đơn hàng chỉ được sử dụng tối đa một voucher. Voucher được áp dụng trên `eligible_subtotal` sau khi đã trừ ưu đãi Flash Sale.
4. Voucher discount cuối cùng phải đảm bảo `0 < discount < subtotal` để tổng tiền thanh toán `total_amount = subtotal - discount > 0` (bảo đảm tính toàn vẹn với `payment_transactions.amount > 0`).

Thách thức chính:
- **Ngăn chặn over-allocation / over-budget dưới tải cao**: Hai khách hàng cùng tranh chấp slot cuối cùng hoặc ngân sách cuối cùng của voucher phải được tuần tự hóa nguyên tử, không vượt hạn mức và không rò rỉ quota khi rollback.
- **Tránh Deadlock giữa các giao dịch đồng thời**: Checkout, Customer Cancel, Staff Confirm, Delivery Complete, Delivery Return và Admin Mutation tác động lên nhiều thực thể khác nhau (Customer, Voucher, Flash Sale, SKU, Inventory, Order). Phải có một **thứ tự khóa (Lock Ordering) toàn cục bất biến**.
- **Tính lũy đẳng và tương thích ngược (Backward Compatibility)**: Replay idempotency key cũ không voucher (`checkout-v1`) phải hoạt động nguyên vẹn; request có voucher dùng canonical payload `checkout-v2`.

---

## 2. Quyết định 1 — Thứ tự Khóa Toàn cục (Lock Ordering)

Để loại trừ hoàn toàn nguy cơ deadlock (chu trình AB-BA), mọi giao dịch tác động lên các tài nguyên liên quan bắt buộc phải tuân thủ thứ tự khóa đơn điệu:

### 2.1 Luồng Checkout (tạo đơn mới)
```text
1. Customer: store.user_accounts FOR NO KEY UPDATE
2. Voucher: store.vouchers FOR UPDATE (nếu checkout có voucher)
3. FlashSaleCampaign: store.flash_sale_campaigns FOR UPDATE (theo thứ tự UUID tăng dần)
4. FlashSaleOffer: store.flash_sale_offers FOR UPDATE (theo thứ tự UUID tăng dần)
5. ProductSku: store.product_skus FOR UPDATE (theo thứ tự UUID tăng dần)
6. InventoryBalance: store.inventory_balances FOR UPDATE (theo thứ tự ProductSku UUID tăng dần)
```

### 2.2 Luồng Đóng/Chuyển trạng thái Order (Terminal transitions)
Áp dụng cho: Customer hủy đơn COD (`CANCELLED`), Worker quét COD quá hạn (`EXPIRED`), Staff hoàn thành giao hàng & thu tiền (`COMPLETED`), Đổi trả hoàn kho (`RETURNED`):
```text
1. SalesOrder: store.sales_orders FOR UPDATE
2. Voucher: store.vouchers FOR UPDATE (nếu Order có voucher redemption)
3. FlashSaleCampaign / Offer (nếu Order có Flash Sale items)
4. ProductSku / InventoryBalance (nếu hoàn kho)
```

### 2.3 Nguyên tắc cách ly
- **Admin Mutation** (Draft / Activate / Pause / Resume / End): Chỉ khóa dòng `store.vouchers FOR UPDATE`, tuyệt đối không khóa Order hay Customer. Không thể tạo chu trình với Checkout hay Order transitions.
- **Preview Voucher** (`POST /api/v1/vouchers/preview`): Chỉ đọc dữ liệu (`READ ONLY`), tính toán mô phỏng giá trên giỏ hàng, **không lock**, **không insert redemption**, và **không giữ quota**.
- Timeout và xử lý deadlock tiếp tục tuân thủ ADR 0002 (`lock_timeout = '5s'`, retry tối đa 3 lần với jitter).

---

## 3. Quyết định 2 — Vòng đời Voucher và Tính Bất biến (Immutability)

Bảng `store.vouchers` quản lý trạng thái voucher qua 4 giá trị:
- `DRAFT`: Khởi tạo ban đầu. Cho phép Admin chỉnh sửa mọi thuộc tính. Không thể sử dụng để preview hay checkout.
- `ACTIVE`: Đã kích hoạt. Có hiệu lực sử dụng khi `starts_at <= now < ends_at`.
- `PAUSED`: Tạm dừng cấp mới. Không nhận preview và không cho phép checkout mới. Các allocation đã ở trạng thái `HELD` trước đó vẫn được bảo toàn.
- `ENDED`: Kết thúc vĩnh viễn (terminal). Không nhận bất kỳ allocation nào mới.

### Quy tắc Bất biến Tài chính (Financial Immutability)
1. **Sau khi Activate lần đầu**:
   - Mã code (`code`), loại giảm giá (`discount_type`), giá trị (`discount_value`), trần giảm (`max_discount_amount`), giá trị đơn tối thiểu (`minimum_order_amount`), tổng hạn mức (`total_usage_limit`), hạn mức mỗi khách (`per_customer_limit`) và tổng ngân sách (`budget_total`) trở thành **bất biến (immutable)**.
   - Admin không được phép thay đổi để tránh ảnh hưởng đến các Order đang giữ chỗ (`HELD`).
2. **Kích hoạt (Activate)**:
   - Bắt buộc kiểm tra toàn bộ điều kiện: `starts_at < ends_at`, `budget_total > 0`, `total_usage_limit > 0`, `1 <= per_customer_limit <= total_usage_limit`, cấu hình discount hợp lệ.
3. **Mở lại (Resume)**:
   - Chỉ cho phép chuyển từ `PAUSED` sang `ACTIVE` nếu thời gian chưa hết (`ends_at > now`), hạn mức chưa cạn (`usage_held + usage_consumed < total_usage_limit`) và ngân sách chưa cạn (`budget_held + budget_consumed < budget_total`).

---

## 4. Quyết định 3 — Vòng đời Allocation (`voucher_redemptions`)

Mỗi lần áp dụng voucher thành công trong checkout, một bản ghi `store.voucher_redemptions` được tạo với trạng thái ban đầu:

```text
       ┌─────────── Checkout thành công ───────────┐
       │                                           │
       ▼                                           │
   [ HELD ] ────────────────────────┐              │
       │                            │              │
       │ Đơn COMPLETED +            │ Đơn CANCELLED /
       │ Thu COD thành công         │ EXPIRED / RETURNED
       ▼                            ▼              │
 [ CONSUMED ]                 [ RELEASED ]         │
  (Terminal)                   (Terminal)          │
                                                   │
  + 1 usage_consumed           - 1 usage_held      │
  + discount budget_consumed   - discount budget_held
  - 1 usage_held               (Trả quota/budget lại)
  - discount budget_held
```

### Các nguyên tắc quản trị Allocation:
1. **HELD**: Chiếm 1 lượt dùng (`usage_held += 1`) và chiếm đúng số tiền chiết khấu trong ngân sách (`budget_held += discount`). Ngân sách khả dụng được tính:
   $$\text{AvailableBudget} = \text{budget\_total} - \text{budget\_held} - \text{budget\_consumed}$$
2. **CONSUMED**: Khi Order đạt trạng thái `COMPLETED` và COD collection thành công:
   - `usage_held -= 1`, `usage_consumed += 1`.
   - `budget_held -= discount`, `budget_consumed += discount`.
3. **RELEASED**: Khi Order bị hủy trước xác nhận (`CANCELLED`), COD hết hạn (`EXPIRED`), hoặc giao thất bại và đã hoàn kho thành công (`RETURNED`):
   - `usage_held -= 1`, `budget_held -= discount`.
   - Trả lại đúng 1 lượt và số tiền chiết khấu cho voucher để khách hàng có thể sử dụng lại.
4. **Idempotency**: Mọi transition `HELD -> CONSUMED` hoặc `HELD -> RELEASED` phải lũy đẳng (idempotent). Nếu redemption đã ở trạng thái đích, thao tác là no-op an toàn.

---

## 5. Quyết định 4 — Canonical Payload và Idempotency (checkout-v1 và checkout-v2)

Để duy trì khả năng replay cho các giao dịch lịch sử không có voucher mà không phá vỡ payload hash:

### 5.1 Legacy / Không dùng voucher: `checkout-v1`
Tiếp tục duy trì định dạng canonical JSON UTF-8 của ADR 0002:
```json
{
  "version": "checkout-v1",
  "shippingAddressId": "uuid-lowercase-d",
  "paymentMethod": "COD",
  "lines": [
    { "productSkuId": "uuid-lowercase-d", "quantity": 1, "flashSaleOfferId": null }
  ]
}
```

### 5.2 Có áp dụng voucher: `checkout-v2`
Thêm trường `voucherCode` đã được normalize (trim + uppercase):
```json
{
  "version": "checkout-v2",
  "voucherCode": "SALE50K",
  "shippingAddressId": "uuid-lowercase-d",
  "paymentMethod": "COD",
  "lines": [
    { "productSkuId": "uuid-lowercase-d", "quantity": 1, "flashSaleOfferId": null }
  ]
}
```

### Quy tắc xử lý Idempotency:
- Cùng `Idempotency-Key` nhưng khác payload hash (ví dụ đổi mã voucher hoặc thêm/bỏ voucher) $\to$ Trả về `409 IDEMPOTENCY_KEY_REUSED`.
- Cùng `Idempotency-Key` và cùng payload hash $\to$ Replay kết quả snapshot đã lưu (`SUCCEEDED` hoặc `REJECTED`) mà không thực hiện lại phân bổ.

---

## 6. Quyết định 5 — Transaction Savepoint và Xử lý Lỗi

Toàn bộ quy trình checkout diễn ra trong một savepoint `checkout_work`:
1. Bắt đầu transaction, đặt `lock_timeout = '5s'`.
2. Khóa Customer `FOR NO KEY UPDATE`.
3. Khóa Voucher `FOR UPDATE` (nếu có).
4. Khóa Campaign/Offer/SKU/Inventory theo thứ tự ID.
5. Revalidate voucher: trạng thái `ACTIVE`, `starts_at <= now < ends_at`, `minimum_order_amount`, per-customer limit (tổng `HELD + CONSUMED < per_customer_limit`), global limit (`usage_held + usage_consumed < total_usage_limit`), và ngân sách khả dụng đủ chiết khấu.
6. Nếu vi phạm bất kỳ điều kiện nghiệp vụ nào:
   - Rollback về `checkout_work`.
   - Lưu trạng thái `REJECTED` vào `checkout_requests` kèm mã lỗi ổn định (ví dụ: `VOUCHER_NOT_ACTIVE`, `VOUCHER_MINIMUM_NOT_MET`, `VOUCHER_USAGE_LIMIT_REACHED`, `VOUCHER_BUDGET_EXHAUSTED`).
   - Commit transaction lưu idempotency log. Không có bất kỳ dòng voucher redemption hay inventory allocation nào bị rò rỉ.
7. Nếu có lỗi hạ tầng (`PostgresException 40P01`, mất kết nối):
   - Rollback toàn bộ transaction để client có thể retry với cùng idempotency key.

---

## 7. Hệ quả

1. **Schema**: Bổ sung bảng `store.vouchers`, `store.voucher_redemptions`, và các cột snapshot trên `store.sales_orders` (`subtotal_amount`, `voucher_discount_amount`, `voucher_code_snapshot`).
2. **Persistence**: EF Core models ánh xạ đầy đủ quan hệ, constraints và indexes.
3. **Checkout Service**: Hỗ trợ đồng thời `checkout-v1` và `checkout-v2`, kiểm soát allocation nguyên tử trong savepoint.
4. **Order Lifecycle**: Tích hợp các điểm nhả quota (`RELEASED`) và chốt quota (`CONSUMED`) trong các dịch vụ hủy đơn, timeout và fulfillment.

