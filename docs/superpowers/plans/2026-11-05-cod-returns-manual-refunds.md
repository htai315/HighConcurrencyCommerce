# Implementation Plan — T39: Đổi/trả hàng và hoàn tiền thủ công cho đơn COD

Ngày chốt kế hoạch: **05/11/2026**  
Trạng thái: **Planned — chưa triển khai**  
Phụ thuộc: T25–T26 Fulfillment, T27 Notifications, T28 Reporting, T31 Customer Order Center, T36 Reviews, T38 Vouchers.  
Không phụ thuộc T19–T22 PayOS; T39 chỉ xử lý đơn `COD` đã thu tiền.

## 1. Mục tiêu

Khép kín vòng đời sau bán hàng cho đơn COD đã giao thành công:

1. Customer gửi một yêu cầu trả hàng theo từng dòng sản phẩm trong vòng 7 ngày từ `Shipment.DeliveredAt`.
2. Admin duyệt hoặc từ chối yêu cầu tài chính.
3. Staff xác nhận đã nhận lại đúng hàng và hoàn kho đúng một lần.
4. Admin ghi nhận chứng từ hoàn tiền thủ công; hệ thống cập nhật Payment, báo cáo doanh thu, review eligibility, audit và notification trong cùng transaction nghiệp vụ.
5. Mọi race/retry không được hoàn kho hai lần, hoàn tiền hai lần, vượt số lượng đã mua hoặc tạo hai yêu cầu cho cùng đơn.

## 2. Phạm vi đã chốt

### 2.1 Có trong T39

- Chỉ áp dụng cho Order `COD`, `COMPLETED`, Shipment `DELIVERED`, Payment `COD_COLLECTED`.
- Cửa sổ yêu cầu: `[delivered_at, delivered_at + 7 ngày]`, đánh giá bằng `TimeProvider` dưới khóa Order.
- Một Order có tối đa **một** return request. Customer chọn một hoặc nhiều OrderItem và số lượng `1..orderedQuantity`.
- Lý do: `WRONG_ITEM`, `WRONG_SIZE`, `DEFECTIVE`, `NOT_AS_DESCRIBED`, `OTHER`; `OTHER` bắt buộc ghi chú.
- Customer được hủy khi request còn `REQUESTED`.
- Admin duyệt/từ chối; Staff xác nhận nhận hàng; Admin xác nhận hoàn tiền thủ công.
- T39 cơ bản chỉ nhập lại các sản phẩm đã được Staff xác nhận đủ điều kiện bán lại. Hàng hỏng/quarantine và đổi sang SKU khác được tách task sau.
- Hoàn tiền có thể toàn phần hoặc một phần theo số lượng trả; không hoàn phí vận chuyển vì hệ thống hiện chưa có shipping fee.

### 2.2 Không có trong T39

- PayOS refund API, webhook refund hoặc reconciliation PayOS.
- Đổi trực tiếp sang SKU khác; Customer trả hàng rồi tạo đơn mới.
- Upload file/object storage; chỉ lưu ghi chú và mã chứng từ/evidence reference.
- Nhiều đợt trả cho cùng một Order.
- Hàng hỏng, kho cách ly, phí restocking, loyalty hoặc voucher bồi thường.

## 3. State machine và quyền

```text
Customer tạo
    REQUESTED
      ├─ Customer cancel ───────────────> CANCELLED
      ├─ Admin reject ──────────────────> REJECTED
      └─ Admin approve ─────────────────> APPROVED
                                               │
                                               └─ Staff nhận hàng + hoàn kho
                                                        RECEIVED
                                                           │
                                                           └─ Admin ghi chứng từ hoàn tiền
                                                                  REFUNDED
```

- `REJECTED`, `CANCELLED`, `REFUNDED` là terminal.
- `REQUESTED -> APPROVED/REJECTED/CANCELLED`, `APPROVED -> RECEIVED`, `RECEIVED -> REFUNDED` là các transition duy nhất.
- Admin không được tự thực hiện bước nhận hàng; Staff không được phê duyệt hoặc xác nhận hoàn tiền.
- Request sai owner trả 404 để không lộ dữ liệu.

## 4. Quy tắc tiền hoàn

### 4.1 Phân bổ discount xác định

Order-level voucher discount phải được phân bổ về OrderItem để partial return không hoàn quá số tiền Customer đã trả:

1. `grossLine = quantity * unitPrice`.
2. Phân bổ `order.voucher_discount_amount` theo tỷ trọng `grossLine / subtotal`.
3. Lấy phần nguyên VND bằng `floor`; phần dư 1 VND được phân lần lượt theo `OrderItem.Id` tăng dần cho đến khi tổng allocation đúng bằng voucher discount.
4. `netLine = grossLine - allocatedVoucherDiscount`.
5. Refund của dòng trả một phần: `floor(netLine * requestedQty / orderedQty)`; trả toàn bộ dòng nhận đúng `netLine`.
6. Tổng refund phải `> 0` và `<= order.total_amount`.

Kết quả được snapshot vào return lines khi tạo request và không thay đổi theo dữ liệu sản phẩm/voucher sau này.

### 4.2 Voucher và Flash Sale

- Voucher redemption đã `CONSUMED` **không được khôi phục**, không giảm `usage_consumed` hay `budget_consumed` sau trả hàng.
- Flash Sale allocation đã `SOLD` **không mở lại quota** sau trả hàng.
- Refund dùng đúng giá thực trả sau Flash Sale và voucher snapshot, không dùng giá niêm yết hiện tại.

### 4.3 Payment và doanh thu

- Khi Staff nhận hàng: Payment `COD_COLLECTED -> REFUND_REQUIRED`.
- Khi Admin hoàn tiền:
  - refund bằng toàn bộ `order.total_amount`: Payment `REFUNDED`;
  - refund nhỏ hơn tổng đơn: Payment `PARTIALLY_REFUNDED`.
- Tận dụng `store.manual_refunds`, mở rộng liên kết tới return request và lưu `evidence_reference`, `amount`, `admin_id`, `refunded_at`.
- Reporting tính doanh thu ròng: verified collected amount trừ `manual_refunds.amount` đã `REFUNDED`; đồng thời trả thêm `grossRevenue`, `refundAmount`, `netRevenue` để không làm mất dấu tiền thu ban đầu.

## 5. Database — migration `011_cod_returns_refunds.sql`

### 5.1 `store.order_return_requests`

- `id`, timestamps, `order_id UNIQUE`, `customer_id`.
- `return_status` enum check: `REQUESTED|APPROVED|REJECTED|CANCELLED|RECEIVED|REFUNDED`.
- `reason_code`, `customer_note` (tối đa 1.000), `requested_at`.
- `decided_by`, `decided_at`, `decision_note`.
- `received_by`, `received_at`, `received_evidence`.
- `refund_amount` nguyên VND dương.
- `revision >= 0`.
- CHECK theo trạng thái: các timestamp/actor/evidence bắt buộc đúng lifecycle và không xuất hiện sớm.

### 5.2 `store.order_return_request_lines`

- `return_request_id`, `order_item_id`, `product_sku_id`.
- `requested_qty > 0`.
- Snapshot: title, SKU code, option, gross amount, allocated voucher discount, refund amount.
- `UNIQUE(return_request_id, order_item_id)`.
- CHECK tiền nguyên VND, `0 < refund_amount <= gross_amount`.

### 5.3 Mở rộng bảng hiện hữu

- Payment status thêm `PARTIALLY_REFUNDED`.
- `manual_refunds`: thêm `return_request_id UNIQUE`, constraint trạng thái/timestamp chặt chẽ và unique evidence reference chuẩn hóa nếu nghiệp vụ yêu cầu.
- Index hàng đợi theo `(return_status, requested_at, id)`.
- Migration phải additive, chạy lại an toàn, backfill không thay đổi đơn lịch sử và được mount trong Compose/fixture.

## 6. Lock order và concurrency

Thứ tự khóa toàn cục cho mọi mutation T39:

```text
SalesOrder
  -> OrderReturnRequest
  -> Payment / PaymentTransaction
  -> ProductSku theo UUID
  -> InventoryBalance theo ProductSku UUID
  -> ProductReview liên quan
```

- Tạo request khóa Order trước, revalidate owner/trạng thái/cửa sổ 7 ngày, kiểm tra unique request và tính snapshot.
- Approve/reject/cancel khóa Order rồi ReturnRequest.
- Receive khóa Order → ReturnRequest → SKU → Inventory; tăng `PhysicalQty` và `AvailableQty`, ghi movement `RETURN_IN_AFTER_DELIVERY` đúng một lần.
- Refund khóa Order → ReturnRequest → Payment/Transaction; insert/update ManualRefund và transition trạng thái trong một transaction.
- Unique violations được chuyển thành `409 RETURN_REQUEST_EXISTS` hoặc `409 REFUND_REFERENCE_REUSED`, không lọt 500.
- Retry cùng transition đã commit trả representation hiện tại hoặc lỗi ổn định; không lặp movement/refund/audit/notification.
- Bổ sung race tests: create/create, approve/cancel, receive/receive, refund/refund và receive/refund.

## 7. Backend contracts và API

### 7.1 Customer — policy `Customer`

- `GET /api/v1/orders/{orderId}/return-eligibility`
- `POST /api/v1/orders/{orderId}/returns`
- `GET /api/v1/account/returns?page=&pageSize=&status=`
- `GET /api/v1/account/returns/{returnId}`
- `POST /api/v1/account/returns/{returnId}/cancel`

### 7.2 Admin — policy `Admin`

- `GET /api/v1/admin/returns?page=&pageSize=&status=&q=`
- `GET /api/v1/admin/returns/{returnId}`
- `POST /api/v1/admin/returns/{returnId}/approve`
- `POST /api/v1/admin/returns/{returnId}/reject`
- `POST /api/v1/admin/returns/{returnId}/refund`

### 7.3 Staff — policy `Staff`

- `GET /api/v1/staff/returns/approved`
- `POST /api/v1/staff/returns/{returnId}/receive`

Mutation bắt buộc CSRF, response `Cache-Control: no-store`, ProblemDetails có `code` và `correlationId`.

Mã lỗi chính: `RETURN_NOT_ELIGIBLE`, `RETURN_WINDOW_EXPIRED`, `RETURN_REQUEST_EXISTS`, `INVALID_RETURN_QUANTITY`, `INVALID_STATE`, `RETURN_NOT_FOUND`, `REFUND_AMOUNT_MISMATCH`, `REFUND_REFERENCE_REUSED`, `INVENTORY_INVARIANT`.

## 8. Tích hợp module hiện hữu

### 8.1 Inventory

- Receive tăng `PhysicalQty += qty`, `AvailableQty += qty`, giữ Reserved/Allocated.
- Ghi movement source `ORDER_RETURN`, source id là ReturnRequest id; unique guard chống movement trùng.

### 8.2 Reviews

- Eligibility chỉ tính số lượng đã giao còn giữ lại sau các return `REFUNDED`.
- Nếu refund làm toàn bộ qualifying line không còn hợp lệ và Customer không có purchase hợp lệ khác cho Product, review hiện hữu chuyển `HIDDEN` với moderation reason hệ thống `RETURN_REFUNDED`; ghi audit trong transaction refund.
- Partial return còn ít nhất một đơn vị không làm mất quyền review.

### 8.3 Notifications và Audit

- Notification event keys duy nhất cho requested/approved/rejected/cancelled/received/refunded.
- Audit actions: `RETURN_REQUESTED`, `RETURN_APPROVED`, `RETURN_REJECTED`, `RETURN_CANCELLED`, `RETURN_RECEIVED`, `RETURN_REFUNDED`.
- Audit snapshot phải gồm order number, item quantities, refund amount, actor, evidence; không lưu PII dư thừa.

### 8.4 Reporting và Order Center

- Revenue report hiển thị gross/refund/net theo ngày thanh toán/hoàn tiền với quy ước được ghi rõ trong API contract.
- Order DTO giữ `OrderStatus=COMPLETED` để bảo toàn lịch sử fulfillment, bổ sung `returnSummary` độc lập.

## 9. Frontend React

### Customer

- OrdersPage: CTA “Yêu cầu trả hàng” chỉ xuất hiện khi đủ điều kiện; dialog chọn dòng/số lượng/lý do và preview tiền hoàn.
- Trang `/account/returns`: danh sách, timeline trạng thái, chi tiết tiền và nút hủy khi còn REQUESTED.
- AccountNavigation thêm mục “Đổi trả”.

### Staff

- Tab “Nhận hàng trả” trong Staff Dashboard: hàng đợi APPROVED, xem snapshots, nhập evidence và xác nhận nhận/hoàn kho bằng ConfirmDialog.

### Admin

- Tab “Đổi trả & Hoàn tiền” trong Admin Dashboard: lọc/tìm kiếm, duyệt/từ chối, ghi mã chứng từ hoàn tiền; hiển thị gross/discount/refund rõ ràng.

Tất cả parser runtime phải strict, loading/empty/error/retry đầy đủ, thao tác pending khóa nút liên quan và có accessible labels/focus management.

## 10. Test matrix bắt buộc

### Backend integration

1. RBAC Guest/Customer/Staff/Admin trên từng endpoint.
2. Owner isolation và 404 cross-owner.
3. Chỉ COD COMPLETED/DELIVERED/COD_COLLECTED trong 7 ngày được tạo.
4. Validation quantity/reason/note và một request mỗi Order.
5. Refund calculation: thường, Flash Sale, voucher fixed/percentage, partial quantity và rounding remainder.
6. State machine hợp lệ/sai trạng thái.
7. Receive exactly-once cập nhật inventory partition và một movement.
8. Refund exactly-once, reference unique, Payment full/partial đúng.
9. Voucher consumed và Flash quota sold không đổi sau refund.
10. Reporting gross/refund/net chính xác.
11. Review eligibility/hide chính xác khi full return; partial return vẫn eligible.
12. Concurrency races nêu tại mục 6, không deadlock/500/double side effect.
13. Migration 011 chạy lặp và bảo toàn dữ liệu cũ.

### Frontend/Vitest

- Customer create/cancel/timeline và error states.
- Staff receive pending lock, CSRF và refresh queue.
- Admin approve/reject/refund, confirmation dialog và duplicate protection.
- Orders/Reporting render đúng số tiền gross/discount/refund/net.

### Playwright E2E thật

`frontend/e2e/07-cod-returns-refunds.spec.ts`:

1. Customer tạo đơn COD và Staff giao thành công.
2. Customer gửi yêu cầu trả một phần.
3. Admin approve.
4. Staff nhận hàng, tồn kho tăng đúng.
5. Admin ghi hoàn tiền.
6. Customer thấy REFUNDED và Admin report thấy refund/net revenue đúng.

## 11. Chia nhỏ triển khai

| ID | Nội dung | Điều kiện hoàn tất |
|---|---|---|
| T39.1 | ADR 0006 + migration 011 + EF mapping | Schema/reapply/constraints tests pass |
| T39.2 | Contracts + refund allocation calculator | Unit/integration rounding tests pass |
| T39.3 | Customer eligibility/create/list/detail/cancel | RBAC, owner, window và concurrency pass |
| T39.4 | Admin approve/reject | State/audit/notification pass |
| T39.5 | Staff receive + inventory exactly-once | Partition/movement/race pass |
| T39.6 | Admin manual refund + Payment lifecycle | Full/partial/ref duplication pass |
| T39.7 | Reporting + Reviews integration | Gross/refund/net và eligibility pass |
| T39.8 | Customer/Staff/Admin frontend | Vitest, lint, build pass |
| T39.9 | Playwright E2E + docs + release gate | Docker E2E thật và toàn suite pass |

## 12. Definition of Done

- Không double return, double stock-in hoặc double refund dưới retry/concurrency.
- Tổng refund không vượt tổng thực trả; mọi amount nguyên VND và snapshot bất biến.
- Inventory invariant luôn đúng.
- Voucher/Flash Sale lifecycle lịch sử không bị viết ngược.
- Review và revenue phản ánh hàng đã hoàn tiền.
- Backend full suite, frontend tests/lint/build, Docker E2E và `graphify update .` đều pass.
- Có `docs/api/returns.md`, ADR 0006, cập nhật README/TASKS/release checklist.

