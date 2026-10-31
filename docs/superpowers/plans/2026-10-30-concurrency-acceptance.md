# T29 — High-concurrency acceptance matrix

Ngày cập nhật: 30/10/2026. Trạng thái tổng: **Partial theo ADR 0003**.

## AC-20a — Không oversell: PASS

Test: `ConcurrencyAcceptanceTests.AC20a_one_hundred_concurrent_buyers_competing_for_ten_units_never_oversell`.

Thiết lập:

- Một SKU `PUBLISHED/ACTIVE`, `physical=10`, `available=10`.
- 100 Customer độc lập, mỗi người có session, địa chỉ, CSRF token và idempotency key riêng.
- Chuẩn bị CSRF trước; 100 POST `/api/v1/checkouts` cùng được nhả qua một start gate.
- Connection pool test giới hạn 32 để tạo backpressure và không vượt PostgreSQL `max_connections`; 100 HTTP request vẫn active đồng thời.

Kết quả bắt buộc và đã đạt:

- Đúng 10 response `201 Created`.
- Đúng 90 response `409 OUT_OF_STOCK`.
- Inventory cuối: `physical=10`, `available=0`, `reserved=10`, `allocated=0`.
- Đúng 10 OrderItem và 10 movement `RESERVE`.
- CheckoutRequest: 10 `SUCCEEDED`, 90 `REJECTED`.
- Không thành phần tồn nào âm, oversell bằng 0.

Đây là acceptance test tính đúng đắn với PostgreSQL thật, không phải benchmark; không suy ra p95/p99, RPS hoặc capacity production.

## AC-20b — Callback trùng: DEFERRED

Phụ thuộc T19–T20: sandbox contract, signature verification, PaymentAttempt mapping và webhook receipt. ADR 0003 cấm chế callback/provider behavior. Chỉ triển khai khi có fixture chữ ký chính thức và sandbox credential.

Điều kiện pass sau khi mở lại: cùng provider transaction/callback gửi 5 lần, đúng một transition/financial transaction có hiệu lực; bốn lần còn lại được nhận diện duplicate an toàn.

## AC-20c — Cancel/expiry vs payment: DEFERRED

Phụ thuộc T20–T22: verified webhook, expiry query-at-deadline, reconciliation/LATE_PAID và cancellation policy PayOS. Không thể chứng minh “tiền thật luôn được ghi” khi provider adapter chưa tồn tại.

Điều kiện pass sau khi mở lại: payment và cancel/expiry chạy qua barrier cạnh tranh; đúng một Order state cuối, tồn/quota chỉ release một lần, mọi khoản thu verified đều được lưu và đối soát kể cả nhánh đến muộn.

## Bằng chứng regression

- Backend: 110/110 integration tests pass, gồm AC-20a mới.
- Frontend không đổi trong T29: 46/46 tests, lint/build từ T28 vẫn pass.
