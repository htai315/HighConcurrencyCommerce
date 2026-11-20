# ADR 0006 — Quản lý Đổi/Trả hàng COD, Hoàn tiền Thủ công và Thứ tự Khóa Chống Deadlock

Trạng thái: **Đã chốt** ngày 05/11/2026. Phạm vi T39; là cơ sở kiến trúc cho quy trình đổi/trả hàng và hoàn tiền thủ công cho đơn COD sau bán hàng.

---

## 1. Bối cảnh

Hệ thống High-Concurrency Commerce đã hoàn thành luồng bán hàng COD từ Giỏ hàng -> Checkout -> Giữ tồn -> Giao hàng -> Thu tiền hoàn tất (`COMPLETED`, `DELIVERED`, `COD_COLLECTED`). Đồng thời đã triển khai Flash Sale và Mã giảm giá cơ bản (Voucher T38).

Yêu cầu nghiệp vụ của T39 là **Đổi/trả hàng và hoàn tiền thủ công cho đơn COD**:
1. Khách hàng (`Customer`) được quyền gửi yêu cầu trả một phần hoặc toàn bộ sản phẩm trong vòng 7 ngày kể từ khi đơn hàng được giao thành công (`Shipment.DeliveredAt`).
2. Mỗi đơn hàng chỉ được tạo tối đa **một** yêu cầu trả hàng (`OrderReturnRequest`).
3. Khách hàng được quyền hủy yêu cầu khi yêu cầu đang ở trạng thái chờ duyệt (`REQUESTED`).
4. Ban quản trị (`Admin`) xem xét, phê duyệt (`APPROVED`) hoặc từ chối (`REJECTED`) yêu cầu.
5. Nhân viên kho (`Staff`) xác nhận đã nhận lại hàng thực tế và thực hiện hoàn kho nguyên tử đúng một lần (`RECEIVED`), ghi nhận `inventory_movements` loại `RETURN_IN_AFTER_DELIVERY`.
6. Ban quản trị (`Admin`) thực hiện chuyển tiền hoàn thủ công ngoài hệ thống (chuyển khoản ngân hàng) và ghi nhận chứng từ hoàn tiền (`ManualRefund`, trạng thái chuyển sang `REFUNDED`).
7. Báo cáo doanh thu (`Reporting`) và tính hợp lệ đánh giá sản phẩm (`Reviews`) phải phản ánh chính xác các khoản hoàn tiền và sản phẩm đã trả lại.

### Thách thức Kỹ thuật
- **Ngăn chặn Double Stock-in và Double Refund**: Dưới tải cao hoặc khi người dùng / mạng retry, không được phép cộng tồn kho hai lần và không được phép ghi nhận hoàn tiền hai lần cho cùng một yêu cầu.
- **Tính toán tiền hoàn chính xác khi có Flash Sale và Voucher**: Voucher giảm giá cấp đơn hàng (Order-level) phải được phân bổ tỷ lệ xác định (deterministic allocation) xuống từng dòng sản phẩm với thuật toán xử lý phần dư làm tròn (remainder distribution) để đảm bảo tổng tiền hoàn cho partial return không bao giờ vượt quá số tiền khách thực trả.
- **Không phục hồi Voucher và Flash Sale Quota**: Voucher đã `CONSUMED` và suất Flash Sale đã `SOLD` khi giao đơn COD thành công là chi phí khuyến mại đã kết thúc, không được phục hồi lại hạn mức sử dụng hay ngân sách khi trả hàng.
- **Loại trừ Deadlock**: Các giao dịch Đổi/Trả tác động lên nhiều thực thể (`SalesOrder`, `OrderReturnRequest`, `Payment`, `PaymentTransaction`, `ProductSku`, `InventoryBalance`, `ProductReview`). Phải tuân thủ một thứ tự khóa toàn cục bất biến.
- **Không phụ thuộc PayOS**: Cổng PayOS tiếp tục ở trạng thái deferred theo ADR 0003; T39 hoàn toàn vận hành trên đơn COD với chứng từ hoàn tiền thủ công.

---

## 2. Quyết định 1 — Thứ tự Khóa Toàn cục (Lock Ordering)

Để loại trừ hoàn toàn nguy cơ deadlock (chu trình khóa AB-BA) giữa các tiến trình đồng thời (ví dụ: Customer tạo/hủy yêu cầu, Admin duyệt/từ chối/hoàn tiền, Staff nhận hàng), mọi giao dịch tác động lên các tài nguyên đổi/trả bắt buộc phải khóa theo thứ tự đơn điệu sau:

```text
1. SalesOrder: store.sales_orders FOR UPDATE
2. OrderReturnRequest: store.order_return_requests FOR UPDATE
3. Payment / PaymentTransaction: store.payments / store.payment_transactions FOR UPDATE
4. ProductSku: store.product_skus FOR UPDATE (theo thứ tự Id UUID tăng dần)
5. InventoryBalance: store.inventory_balances FOR UPDATE (theo thứ tự ProductSku UUID tăng dần)
6. ProductReview: store.product_reviews FOR UPDATE (nếu cần ẩn review khi full return)
```

### Nguyên tắc cụ thể cho từng thao tác:
- **Customer Tạo Yêu cầu (`POST /api/v1/orders/{orderId}/returns`)**:
  - Khóa `SalesOrder FOR UPDATE`.
  - Re-validate điều kiện: đúng chủ sở hữu (`customer_id`), trạng thái `COMPLETED`, phương thức `COD`, thanh toán `COD_COLLECTED`, và trong cửa sổ `[delivered_at, delivered_at + 7 ngày]`.
  - Kiểm tra xem đã có `OrderReturnRequest` nào cho đơn hàng này chưa (`order_id UNIQUE`). Nếu trùng, trả về `409 RETURN_REQUEST_EXISTS`.
  - Tính toán snapshot phân bổ tiền hoàn và insert `OrderReturnRequest` + `OrderReturnRequestLines`.
- **Customer Hủy Yêu cầu (`POST /api/v1/account/returns/{returnId}/cancel`)**:
  - Khóa `SalesOrder FOR UPDATE` -> Khóa `OrderReturnRequest FOR UPDATE`.
  - Kiểm tra `customer_id` và trạng thái `REQUESTED -> CANCELLED`.
- **Admin Phê duyệt / Từ chối (`POST .../approve`, `POST .../reject`)**:
  - Khóa `SalesOrder FOR UPDATE` -> Khóa `OrderReturnRequest FOR UPDATE`.
  - Kiểm tra trạng thái hiện tại là `REQUESTED`. Chuyển sang `APPROVED` hoặc `REJECTED`. Ghi `store.audit_logs`.
- **Staff Nhận hàng & Hoàn kho (`POST /api/v1/staff/returns/{returnId}/receive`)**:
  - Khóa `SalesOrder` -> Khóa `OrderReturnRequest` -> Khóa `Payment` -> Khóa `ProductSkus` (UUID tăng dần) -> Khóa `InventoryBalances` (UUID tăng dần).
  - Kiểm tra trạng thái `APPROVED -> RECEIVED`.
  - Tăng `physical_qty += requested_qty`, `available_qty += requested_qty`, giữ nguyên `reserved_qty` và `allocated_qty`.
  - Ghi nhận `store.inventory_movements` với `movement_type = 'RETURN_IN_AFTER_DELIVERY'`, `source_type = 'ORDER_RETURN'`, `source_id = return_request.id`.
  - Chuyển `Payment.payment_status = 'REFUND_REQUIRED'`.
- **Admin Hoàn tiền Thủ công (`POST /api/v1/admin/returns/{returnId}/refund`)**:
  - Khóa `SalesOrder` -> Khóa `OrderReturnRequest` -> Khóa `Payment` -> Khóa `PaymentTransaction` -> Khóa `ProductReview` (nếu cần).
  - Kiểm tra trạng thái `RECEIVED -> REFUNDED`.
  - Ghi nhận `store.manual_refunds` với `return_request_id`, `amount = return_request.refund_amount`, `evidence_reference`.
  - Nếu `refund_amount == sales_order.total_amount`: chuyển `Payment.payment_status = 'REFUNDED'`.
  - Nếu `refund_amount < sales_order.total_amount`: chuyển `Payment.payment_status = 'PARTIALLY_REFUNDED'`.
  - Nếu toàn bộ số lượng mua của một sản phẩm trong đơn đã được trả hết và khách không còn đơn mua thành công nào khác cho sản phẩm đó: chuyển `ProductReview` tương ứng sang `HIDDEN` với lý do hệ thống `RETURN_REFUNDED`.

---

## 3. Quyết định 2 — Vòng đời State Machine và Phân quyền

```text
Customer tạo:
   REQUESTED
     ├─ Customer cancel ───────────────> CANCELLED (terminal)
     ├─ Admin reject ──────────────────> REJECTED (terminal)
     └─ Admin approve ─────────────────> APPROVED
                                             │
                                             └─ Staff nhận hàng + hoàn kho:
                                                    RECEIVED
                                                       │
                                                       └─ Admin ghi chứng từ hoàn tiền:
                                                              REFUNDED (terminal)
```

- **Quyền hạn theo Role**:
  - `Customer`: Xem danh sách/chi tiết đơn hàng của chính mình, kiểm tra điều kiện trả hàng, gửi yêu cầu trả hàng, hủy yêu cầu khi còn `REQUESTED`. Không được xem hoặc thao tác đơn của khách hàng khác (trả 404 để bảo mật).
  - `Staff`: Chỉ có quyền xem danh sách đơn trả đã được duyệt (`APPROVED`), kiểm tra sản phẩm thực tế, ghi nhận chứng từ nhận hàng (`received_evidence`), và bấm xác nhận nhận hàng / hoàn kho (`RECEIVED`). Staff **không có quyền** duyệt yêu cầu, từ chối yêu cầu, hoặc ghi nhận hoàn tiền.
  - `Admin`: Có toàn quyền duyệt (`APPROVED`), từ chối (`REJECTED`) và ghi nhận hoàn tiền (`REFUNDED`). Admin **không được** tự thực hiện bước nhận hàng thay Staff để đảm bảo tính phân tách trách nhiệm vật lý và kế toán (Separation of Concerns).

---

## 4. Quyết định 3 — Thuật toán Phân bổ Tiền hoàn (Refund Allocation)

Khách hàng có thể trả toàn phần hoặc một phần (partial return). Để ngăn chặn việc trả một phần hàng nhưng nhận số tiền hoàn vượt quá số tiền thực trả khi đơn có áp dụng Voucher:

1. **Gross Line**:
   $$\text{grossLine} = \text{orderedQuantity} \times \text{unitPrice}$$
2. **Tỷ trọng Voucher Discount**:
   $$\text{rawVoucherShare}_i = \frac{\text{grossLine}_i}{\text{order.subtotal\_amount}} \times \text{order.voucher\_discount\_amount}$$
3. **Phần nguyên và phân bổ phần dư (Remainder Distribution)**:
   - Lấy phần nguyên VND: $\text{floorDiscount}_i = \lfloor \text{rawVoucherShare}_i \rfloor$.
   - Tính tổng phần nguyên: $\text{sumFloor} = \sum \text{floorDiscount}_i$.
   - Phần dư cần phân bổ: $\text{remainder} = \text{order.voucher\_discount\_amount} - \text{sumFloor}$.
   - Duyệt các dòng `OrderItem` theo `Id` tăng dần, cộng thêm 1 VND cho mỗi dòng cho đến khi hết `remainder`.
   - Kết quả ta có: $\text{allocatedVoucherDiscount}_i$ với $\sum \text{allocatedVoucherDiscount}_i = \text{order.voucher\_discount\_amount}$.
4. **Net Line**:
   $$\text{netLine}_i = \text{grossLine}_i - \text{allocatedVoucherDiscount}_i$$
5. **Tiền hoàn cho số lượng trả (`requestedQty`)**:
   - Nếu $\text{requestedQty}_i = \text{orderedQuantity}_i$: nhận trọn vẹn $\text{lineRefund} = \text{netLine}_i$.
   - Nếu $\text{requestedQty}_i < \text{orderedQuantity}_i$:
     $$\text{lineRefund} = \left\lfloor \frac{\text{netLine}_i \times \text{requestedQty}_i}{\text{orderedQuantity}_i} \right\rfloor$$
6. **Tổng tiền hoàn**:
   $$\text{totalRefund} = \sum \text{lineRefund}_i$$
   Đảm bảo: $0 < \text{totalRefund} \le \text{order.total\_amount}$.

Toàn bộ các giá trị tính toán trên được snapshot bất biến vào `store.order_return_request_lines` ngay tại thời điểm tạo yêu cầu.

---

## 5. Quyết định 4 — Xử lý Voucher, Flash Sale, Tồn kho và Đánh giá

1. **Voucher & Flash Sale**:
   - `store.vouchers`: Giữ nguyên `usage_consumed` và `budget_consumed`. Không giảm hay hoàn quota.
   - `store.flash_sale_campaigns`: Giữ nguyên số lượng đã bán. Không phục hồi quota Flash Sale.
2. **Tồn kho (`store.inventory_balances`)**:
   - Khi Staff bấm nhận hàng (`RECEIVED`): `physical_qty += requested_qty`, `available_qty += requested_qty`.
   - `reserved_qty` và `allocated_qty` không thay đổi.
   - Insert bản ghi `store.inventory_movements` loại `RETURN_IN_AFTER_DELIVERY`.
3. **Đánh giá Sản phẩm (`store.product_reviews`)**:
   - Đánh giá chỉ hợp lệ khi khách hàng có đơn hàng đã giao và còn giữ lại ít nhất 1 sản phẩm.
   - Nếu yêu cầu hoàn tiền thành công (`REFUNDED`) mà toàn bộ số lượng đã mua của SKU bị trả hết, hệ thống kiểm tra xem khách còn đơn `COMPLETED` nào khác chứa sản phẩm này không. Nếu không còn, review hiện hữu (nếu có) được tự động chuyển sang `HIDDEN` với lý do `RETURN_REFUNDED` và ghi Audit Log.
   - Trả hàng một phần (vẫn còn giữ ít nhất 1 sản phẩm) không ảnh hưởng tới quyền đánh giá.

---

## 6. Hậu quả & Đánh giá

### Tích cực:
- Khép kín toàn bộ chu trình sau bán lẻ cho đơn COD, đáp ứng đầy đủ yêu cầu thực tế của thương mại điện tử.
- Loại trừ triệt để khả năng double stock-in, double refund và deadlock nhờ thứ tự khóa đơn điệu và unique constraints.
- Thuật toán phân bổ tiền hoàn bảo đảm an toàn dòng tiền, không thất thoát ngân sách khi có khuyến mại phức tạp.
- Đảm bảo tính minh bạch kiểm toán với Separation of Concerns giữa Staff (kho) và Admin (tài chính).

### Tiêu cực & Biện pháp khắc phục:
- Chưa hỗ trợ đổi hàng trực tiếp sang SKU khác: Khách hàng trả hàng nhận tiền hoàn rồi tạo đơn mới; tính năng đổi SKU khác được xếp vào roadmap sau.
- Chứng từ chuyển khoản ngân hàng do Admin nhập mã tham chiếu (`evidence_reference`), chưa tích hợp tự động qua cổng thanh toán: Phù hợp với quyết định defer PayOS theo ADR 0003.

