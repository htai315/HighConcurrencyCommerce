# COD Returns & Manual Refunds API Specification (T39)

Tài liệu đặc tả các giao diện API quản lý và vận hành quy trình Đổi/Trả hàng và Hoàn tiền thủ công cho đơn thanh toán khi nhận hàng (COD) trong hệ thống High-Concurrency E-Commerce.

---

## 1. Tổng quan Nghiệp vụ

- **Phạm vi áp dụng**:
  - Chỉ áp dụng cho đơn hàng COD (`SalesOrder.PaymentMethod = 'COD'`) đã hoàn tất giao hàng thành công: `SalesOrder.OrderStatus = 'COMPLETED'`, `Shipment.ShipmentStatus = 'DELIVERED'`, `Payment.PaymentStatus = 'COD_COLLECTED'`.
  - Cửa sổ đổi/trả: Trong vòng 7 ngày kể từ thời điểm giao hàng thành công (`Shipment.DeliveredAt`).
  - Tối đa 1 yêu cầu đổi/trả cho mỗi đơn hàng (`UNIQUE (order_id)`).
- **Hỗ trợ trả toàn phần và một phần**:
  - Khách hàng có thể chọn từng mặt hàng (`order_item_id`) và số lượng muốn trả (`1 <= requested_qty <= ordered_qty`).
  - Lý do đổi/trả (`reason_code`): `WRONG_ITEM`, `WRONG_SIZE`, `DEFECTIVE`, `NOT_AS_DESCRIBED`, `OTHER` (bắt buộc nhập ghi chú khi chọn `OTHER`).
- **Quy tắc phân bổ số tiền hoàn (`Refund Allocation Calculator`)**:
  - Tiền hoàn trả tính trên số tiền khách **thực trả sau khi trừ Flash Sale và Voucher**, snapshot tại thời điểm tạo yêu cầu.
  - Phí vận chuyển là dịch vụ đã thực hiện và **không được hoàn**. Trần hoàn của toàn bộ hàng hóa là `subtotalAmount - voucherDiscountAmount`, không phải `orderTotalAmount` vốn đã gồm `shippingFee`.
  - Phân bổ chiết khấu voucher tỷ lệ theo giá trị gross của từng dòng:
    $$\text{lineVoucherDiscount} = \left\lfloor \frac{\text{itemGross}}{\text{orderSubtotal}} \times \text{orderDiscount} \right\rfloor$$
  - Phần dư 1 VNĐ làm tròn được phân bổ tuần tự cho các dòng theo `OrderItem.Id` tăng dần.
  - Giá trị hoàn trả của dòng trả một phần:
    $$\text{lineRefund} = \left\lfloor \frac{\text{netLineAmount} \times \text{requestedQty}}{\text{orderedQty}} \right\rfloor$$
  - Nếu một dòng có giá trị thực trả bằng 0 sau phân bổ khuyến mại, API từ chối chọn dòng đó với `400 ITEM_HAS_NO_REFUNDABLE_AMOUNT`; hệ thống không tự tạo khoản hoàn tối thiểu 1 VNĐ.
  - DTO eligibility/detail trả riêng `shippingFee` và `orderTotalAmount` để UI giải thích minh bạch phần không hoàn.
- **Bất biến hệ thống**:
  - Voucher đã `CONSUMED` và quota Flash Sale đã `SOLD` **không bao giờ được phục hồi** khi trả hàng.
  - Nhận hàng đúng một lần (`RETURN_IN`) vào phân vùng cách ly; hàng chỉ trở lại khả dụng sau kiểm định.
  - Bất biến: `Physical = Available + Reserved + Allocated + Quarantined + Damaged`.
  - Trạng thái thanh toán: `COD_COLLECTED` -> `REFUND_REQUIRED` (khi Staff nhận hàng) -> `PARTIALLY_REFUNDED` hoặc `REFUNDED` (khi Admin ghi nhận chứng từ hoàn tiền).
  - Đánh giá sản phẩm: Khi khách trả toàn bộ số lượng của sản phẩm trong đơn, mọi đánh giá đã duyệt sẽ chuyển sang ẩn (`HIDDEN`, lý do `RETURN_REFUNDED`).
- **Thứ tự khóa tài nguyên (Lock Ordering chống deadlock)**:
  1. `SalesOrder` (`store.sales_orders` theo `order_id`)
  2. `OrderReturnRequest` (`store.order_return_requests`)
  3. `Payment` / `PaymentTransaction` (`store.payments`, `store.payment_transactions`)
  4. `ProductSku` (`store.product_skus` theo Id tăng dần)
  5. `InventoryBalance` (`store.inventory_balances` theo `product_sku_id` tăng dần)
  6. `ProductReview` (`store.product_reviews` theo Id tăng dần)

---

## 2. Customer Returns API

### 2.1. Tra cứu điều kiện đổi trả của đơn hàng (`GET /api/v1/orders/{orderId}/return-eligibility`)
Kiểm tra xem đơn hàng còn trong hạn 7 ngày, đúng trạng thái và trả về danh sách các sản phẩm có thể yêu cầu đổi trả.

- **Quyền hạn**: Authenticated Customer (chỉ xem đơn của chính mình).
- **Response 200 OK**:
```json
{
  "orderId": "b1b01dfc-f836-419b-a78b-d7d8e62d4a11",
  "orderNumber": "ORD-20261105-001",
  "isEligible": true,
  "ineligibilityReason": null,
  "deliveredAt": "2026-11-04T10:00:00Z",
  "windowExpiresAt": "2026-11-11T10:00:00Z",
  "items": [
    {
      "orderItemId": "8a3a0e37-9cb8-4f27-a06f-4318357fbb1a",
      "productSkuId": "2c92e921-6b83-4a11-b0db-6e11893c0d89",
      "productTitle": "Vợt cầu lông Pro Attack 900",
      "skuCode": "VOT-PRO-900-4U",
      "optionLabel": "Trọng lượng: 4U (83g)",
      "orderedQuantity": 1,
      "maxReturnableQuantity": 1,
      "unitPrice": 1500000.0,
      "estimatedLineRefund": 1450000.0
    }
  ]
}
```

### 2.2. Gửi yêu cầu đổi/trả hàng (`POST /api/v1/orders/{orderId}/returns`)
Tạo yêu cầu đổi trả cho một hoặc nhiều mặt hàng trong đơn COD đã giao thành công.

- **Quyền hạn**: Authenticated Customer (chỉ tạo cho đơn của chính mình).
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Request Body**:
```json
{
  "reasonCode": "DEFECTIVE",
  "customerNote": "Khung vợt bị nứt nhẹ khi mở hộp kiểm tra.",
  "lines": [
    {
      "orderItemId": "8a3a0e37-9cb8-4f27-a06f-4318357fbb1a",
      "requestedQty": 1
    }
  ]
}
```
- **Response 201 Created**:
```json
{
  "id": "e645938f-a9cb-4c6e-81fa-22f1680ab901",
  "orderId": "b1b01dfc-f836-419b-a78b-d7d8e62d4a11",
  "orderNumber": "ORD-20261105-001",
  "returnStatus": "REQUESTED",
  "reasonCode": "DEFECTIVE",
  "customerNote": "Khung vợt bị nứt nhẹ khi mở hộp kiểm tra.",
  "refundAmount": 1450000.0,
  "requestedAt": "2026-11-05T08:00:00Z",
  "decidedAt": null,
  "decisionNote": null,
  "receivedAt": null,
  "refundedAt": null,
  "lines": [
    {
      "id": "73d6bb6c-1123-4567-89ab-cdef01234567",
      "orderItemId": "8a3a0e37-9cb8-4f27-a06f-4318357fbb1a",
      "productSkuId": "2c92e921-6b83-4a11-b0db-6e11893c0d89",
      "productTitle": "Vợt cầu lông Pro Attack 900",
      "skuCode": "VOT-PRO-900-4U",
      "optionLabel": "Trọng lượng: 4U (83g)",
      "requestedQty": 1,
      "unitPrice": 1500000.0,
      "grossAmount": 1500000.0,
      "allocatedVoucherDiscount": 50000.0,
      "refundAmount": 1450000.0
    }
  ],
  "createdAt": "2026-11-05T08:00:00Z",
  "updatedAt": "2026-11-05T08:00:00Z"
}
```

### 2.3. Xem danh sách yêu cầu đổi/trả của tôi (`GET /api/v1/account/returns?page=1&pageSize=20`)
- **Quyền hạn**: Authenticated Customer.
- Khi đã hoàn tiền, mỗi item trả thêm `evidenceReference` để khách hàng đối chiếu chứng từ.
- **Response 200 OK**:
```json
{
  "items": [ ... ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

### 2.4. Xem chi tiết yêu cầu đổi/trả (`GET /api/v1/account/returns/{id}`)
- **Quyền hạn**: Authenticated Customer (owner isolation).

### 2.5. Hủy yêu cầu đổi/trả (`POST /api/v1/account/returns/{id}/cancel`)
Hủy yêu cầu khi vẫn đang ở trạng thái `REQUESTED`. Khi đã được duyệt (`APPROVED`) hoặc đã nhận (`RECEIVED`), khách không thể hủy.

- **Quyền hạn**: Authenticated Customer (owner isolation).
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Response 200 OK**: Trả về `CustomerReturnDetailDto` với `returnStatus: "CANCELLED"`.

---

## 3. Staff Returns API

### 3.1. Xem danh sách chờ tiếp nhận hàng trả (`GET /api/v1/staff/returns/approved?page=1&pageSize=50`)
Liệt kê các yêu cầu đã được Admin phê duyệt (`APPROVED`), sắp xếp FIFO theo thời gian phê duyệt.

- **Quyền hạn**: Chỉ role `STAFF` (phân tách nhiệm vụ; `ADMIN` bị từ chối).
- **Response 200 OK**:
```json
{
  "items": [
    {
      "id": "e645938f-a9cb-4c6e-81fa-22f1680ab901",
      "orderId": "b1b01dfc-f836-419b-a78b-d7d8e62d4a11",
      "orderNumber": "ORD-20261105-001",
      "customerId": "8935cbb7-1111-4a11-80db-6e11893c0001",
      "customerEmail": "khachhang@example.invalid",
      "customerFullName": "Nguyễn Văn A",
      "returnStatus": "APPROVED",
      "reasonCode": "DEFECTIVE",
      "customerNote": "Khung vợt bị nứt",
      "refundAmount": 1450000.0,
      "requestedAt": "2026-11-05T08:00:00Z",
      "decidedAt": "2026-11-05T08:30:00Z",
      "decisionNote": "Đồng ý nhận hàng kiểm tra",
      "linesCount": 1,
      "totalUnits": 1
    }
  ],
  "page": 1,
  "pageSize": 50,
  "totalCount": 1
}
```

### 3.2. Xác nhận nhận hàng vào khu cách ly (`POST /api/v1/staff/returns/{id}/receive`)
Xác nhận đã nhận hàng thực tế. Hệ thống tăng `physical_qty` và `quarantined_qty`, không thay đổi `available_qty`, ghi movement `RETURN_IN`; Payment chuyển thành `REFUND_REQUIRED`.

- **Quyền hạn**: Role `STAFF` hoặc `ADMIN`.
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Request Body**:
```json
{
  "receivedEvidence": "Biên bản kiểm tra BB-20261105-01: Hàng nứt đúng mô tả, phụ kiện nguyên vẹn."
}
```
- **Response 200 OK**: Trả về `StaffReturnDetailDto` với `returnStatus: "RECEIVED"`, `receivedAt` và `receivedBy`.

### 3.3. Xem chi tiết hàng trả (`GET /api/v1/staff/returns/{id}`)

Trả chi tiết các dòng và snapshot số lượng cần phân loại. Chỉ role `STAFF`.

### 3.4. Hoàn tất kiểm định (`POST /api/v1/staff/returns/{id}/inspect`)

```json
{
  "inspectionNote": "Một sản phẩm đủ điều kiện bán lại",
  "lines": [{
    "returnLineId": "73d6bb6c-1123-4567-89ab-cdef01234567",
    "restockQty": 1,
    "quarantineQty": 0,
    "damagedQty": 0,
    "scrappedQty": 0
  }]
}
```

Mọi dòng phải xuất hiện đúng một lần và tổng bốn disposition phải bằng `requestedQty`. Replay cùng payload là idempotent; payload khác trả `409 IDEMPOTENCY_CONFLICT`. Thành công trả `returnStatus: "INSPECTED"`.

---

## 4. Admin Returns API

### 4.1. Danh sách yêu cầu đổi/trả (`GET /api/v1/admin/returns?status=REQUESTED&page=1&pageSize=50`)
- **Quyền hạn**: Role `ADMIN`.
- **Query params**: `status` (tùy chọn), `searchTerm` (tìm theo mã đơn hoặc email/họ tên khách), `page`, `pageSize`.

### 4.2. Phê duyệt yêu cầu đổi/trả (`POST /api/v1/admin/returns/{id}/approve`)
- **Quyền hạn**: Role `ADMIN`.
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Request Body**:
```json
{
  "decisionNote": "Đồng ý tiếp nhận hàng gửi lại về kho kiểm tra."
}
```
- **Response 200 OK**: Trả về `AdminReturnDetailDto` với `returnStatus: "APPROVED"`.

### 4.3. Từ chối yêu cầu đổi/trả (`POST /api/v1/admin/returns/{id}/reject`)
- **Quyền hạn**: Role `ADMIN`.
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Request Body**:
```json
{
  "decisionNote": "Sản phẩm không có lỗi từ nhà sản xuất theo hình ảnh cung cấp."
}
```
- **Response 200 OK**: Trả về `AdminReturnDetailDto` với `returnStatus: "REJECTED"`.

### 4.4. Ghi nhận chứng từ hoàn tiền thủ công (`POST /api/v1/admin/returns/{id}/refund`)
Sau khi Staff hoàn tất kiểm định (`INSPECTED`), kế toán/admin thực hiện chuyển khoản hoặc hoàn tiền mặt bên ngoài và nhập mã chứng từ giao dịch thực tế vào hệ thống. Trạng thái `RECEIVED` chưa được phép hoàn tiền.
Hệ thống tự động:
- Kiểm tra tính duy nhất của mã chứng từ (`manual_refunds.evidence_reference UNIQUE`).
- Cập nhật trạng thái Payment sang `REFUNDED` (nếu hoàn toàn bộ tiền đã thu) hoặc `PARTIALLY_REFUNDED` (nếu hoàn một phần).
- Tạo bản ghi `ManualRefund` liên kết với payment transaction thu tiền gốc và yêu cầu trả hàng.
- Ẩn đánh giá sản phẩm tương ứng nếu khách không còn giữ đơn vị nào của sản phẩm đó.
- Ghi Audit Log hành động `RETURN_REFUNDED`.

- **Quyền hạn**: Role `ADMIN`.
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Request Body**:
```json
{
  "evidenceReference": "UNC-VCB-20261105-889922",
  "note": "Ủy nhiệm chi hoàn tiền qua Vietcombank cho khách hàng."
}
```
- **Response 200 OK**: Trả về `AdminReturnDetailDto` với `returnStatus: "REFUNDED"`, `evidenceReference` và `refundedAt`.

---

## 5. Reporting Integration API

Các số liệu đổi/trả và hoàn tiền được tích hợp trực tiếp vào báo cáo tài chính quản trị tại `GET /api/v1/admin/reports/revenue`:

- `grossRevenue`: Tổng doanh thu trước khi trừ hoàn tiền.
- `refundAmount`: Tổng số tiền đã hoàn trả cho khách qua các đơn đổi/trả đã hoàn tất (`REFUNDED`).
- `netRevenue`: Doanh thu thuần thực tế = $\text{grossRevenue} - \text{refundAmount}$; có thể âm khi tiền hoàn trong kỳ lớn hơn tiền thu trong kỳ.
- `approvedReturnsCount`: Số lượng yêu cầu đổi/trả đã được phê duyệt.

