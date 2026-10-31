# ADR 0002 — Checkout idempotency nguyên tử và trạng thái VOID dùng chung

Trạng thái: **Đã chốt theo ủy quyền của chủ dự án** ngày 30/10/2026. Phạm vi T16; là đầu vào bắt buộc cho T17–T21.

## Bối cảnh

Schema đã có `checkout_requests` với ba trạng thái `PROCESSING`, `SUCCEEDED`, `REJECTED` và Payment có `VOID`, `COD_PENDING`. Hai điểm chưa rõ là cách replay lỗi/timeout mà không tạo Order trùng, và trạng thái cuối của Payment COD khi đơn đóng trước khi thu tiền.

## Quyết định 1 — CheckoutRequest là idempotency record nguyên tử

- Scope key là `(customer_id, idempotency_key)`. Header `Idempotency-Key` bắt buộc, 8–160 ký tự ASCII thuộc `[A-Za-z0-9._:-]`.
- Cùng key và khác `payload_hash` luôn trả `409 IDEMPOTENCY_KEY_REUSED`; không thực thi nghiệp vụ.
- `SUCCEEDED` replay body đã commit, HTTP 200 và header `Idempotency-Replayed: true`. Lần tạo đầu trả 201.
- `REJECTED` replay cùng HTTP status/error code nghiệp vụ đã lưu. `correlationId` luôn tạo mới, không lưu/replay trace cũ.
- Lỗi validation cú pháp/model, auth, CSRF và idempotency-key format xảy ra trước workflow nên không tạo `CheckoutRequest`.
- Lỗi nghiệp vụ xác định được sau khi nhận workflow — ví dụ hết tồn, SKU không bán, quota/limit COD — được lưu `REJECTED`. Muốn thử một ý định checkout mới sau khi dữ liệu thay đổi, client phải sinh key mới.
- Lỗi hạ tầng/transient (`40P01`, `40001`, mất kết nối, 5xx nội bộ) không được chuyển thành `REJECTED`; transaction rollback toàn bộ để cùng key có thể retry.

### Transaction và savepoint

1. Chuẩn hóa request và tính hash trước transaction.
2. `BEGIN`, đặt lock timeout hữu hạn, khóa Customer `FOR UPDATE`.
3. Đọc `CheckoutRequest` theo key và áp replay/conflict nếu đã có.
4. Tạo `PROCESSING`, sau đó tạo savepoint `checkout_work`.
5. Khóa Offer theo ID tăng dần, rồi SKU/Inventory theo SKU ID tăng dần; kiểm tra toàn bộ rule.
6. Thành công: tạo Order/Items/Payment/Reservation/Allocation, cập nhật tồn/quota, đặt `SUCCEEDED` + `order_id` + response snapshot rồi commit một lần.
7. Business rejection: rollback về savepoint để xóa mọi side effect nhưng giữ idempotency row; đặt `REJECTED` + error snapshot rồi commit.
8. Transient/unknown failure: rollback toàn bộ transaction, kể cả `CheckoutRequest`.

Không được commit trạng thái `PROCESSING` như một bước trung gian. Vì vậy crash trước commit không để lại row lửng; crash sau commit đã có trạng thái cuối để replay.

### Concurrent request và timeout

- Customer lock tuần tự hóa checkout cùng tài khoản, đồng thời bảo vệ giới hạn hai COD pending.
- Request cùng key đang chạy sẽ chờ lock trong giới hạn cấu hình. Hết thời gian chờ trả `409 CHECKOUT_IN_PROGRESS` kèm `Retry-After`; không chạy song song và không thay đổi row.
- Nếu gặp một row `PROCESSING` đã commit do dữ liệu legacy/thao tác vận hành, trả `409 CHECKOUT_IN_PROGRESS`. Không tự takeover hay đổi thành `REJECTED` chỉ dựa trên tuổi row vì timeout không chứng minh transaction/side effect bên ngoài đã thất bại.
- Deadlock/serialization failure được retry toàn bộ transaction tối đa 3 lần với jitter ngắn. Lock timeout và kết quả commit không rõ ràng không được retry mù; client retry cùng key để phân giải.

### Canonical payload `checkout-v1`

Hash SHA-256 lowercase của JSON UTF-8 có property order cố định:

```json
{
  "version": "checkout-v1",
  "shippingAddressId": "uuid-lowercase-d",
  "paymentMethod": "COD|PAYOS",
  "lines": [
    { "productSkuId": "uuid-lowercase-d", "quantity": 1, "flashSaleOfferId": null }
  ]
}
```

Lines phải có SKU duy nhất và được sort ordinal theo `productSkuId`; quantity là integer. Client không gửi giá/tổng/status. Cart thay đổi sau request không thay đổi replay của key cũ vì ý định mua đã nằm trong payload.

Response snapshot final chỉ chứa dữ liệu API an toàn:

- `SUCCEEDED`: HTTP status, Order ID/number/status/deadline/total và dữ liệu cần trả cho client.
- `REJECTED`: HTTP status, stable error code và safe message/metadata; không lưu stack trace, secret hoặc correlation ID.

## Quyết định 2 — PayOS tách khỏi CheckoutRequest

- Checkout PayOS tạo Order `PENDING_PAYMENT`, Payment `PENDING` và reservation trong DB; không gọi PayOS khi transaction checkout đang mở.
- Tạo link nằm ở endpoint PaymentAttempt riêng. PayOS timeout không đổi CheckoutRequest `SUCCEEDED` thành `REJECTED` và không tự nhả reservation.
- Mỗi attempt có `provider_order_code` ổn định. Kết quả external không rõ ràng giữ attempt ở `CREATED` và chặn tạo attempt mới cho đến khi tra cứu/phân giải; không đánh dấu `FAILED` chỉ vì client timeout.
- Trước khi T19 xác minh sandbox/official contract, mỗi request chỉ gọi provider một lần, không retry mù. Sau T19 chỉ được tăng bounded retry nếu PayOS xác nhận cùng `provider_order_code` là idempotent; vẫn không quá giới hạn ba business attempts trong 15 phút.

## Quyết định 3 — `VOID` là terminal chưa thu tiền cho cả COD và PayOS

`VOID` được định nghĩa độc lập provider: **nghĩa vụ thanh toán đã đóng mà chưa có khoản thu được xác minh**.

- Tạo Order COD: Payment `COD_PENDING`.
- Staff xác nhận COD: Order sang `CONFIRMED`, Payment vẫn `COD_PENDING`.
- Giao thành công và xác nhận thực thu: ghi PaymentTransaction provider `COD`, Payment `COD_COLLECTED` trong cùng transaction với trạng thái giao hàng/Order theo rule T25.
- COD bị Customer/Staff/Admin hủy trước khi thu: Order `CANCELLED`, Payment `COD_PENDING -> VOID`, reservation/quota release atomically.
- COD hết 24 giờ chưa xác nhận: Order `EXPIRED`, Payment `COD_PENDING -> VOID`, release atomically exactly once.
- Nếu phát hiện tiền thật sau `VOID`, ghi transaction và chuyển `LATE_PAID` + reconciliation; không hồi sinh Order hoặc tái giữ kho.

Không thêm `COD_CANCELLED`, không dùng `FAILED` cho hủy hợp lệ, và không để `COD_PENDING` trên Order đã terminal.

## Hệ quả

- T17 phải nhận explicit checkout lines và triển khai savepoint; không checkout trực tiếp từ cart ngầm.
- T18/T21 dùng guarded transition Order + Payment + Reservation trong một transaction.
- Schema trạng thái hiện tại đủ dùng; chưa cần thêm Payment status hoặc bảng idempotency mới.
- Integration tests bắt buộc: cùng key/cùng payload, cùng key/khác payload, concurrent same key, business rejection replay, transient rollback/retry, lock timeout, COD cancel/expiry sang VOID và late collection không hồi sinh Order.
